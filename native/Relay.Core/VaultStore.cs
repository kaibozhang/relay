using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// Local JSON vault. Call Load before editing; Save also loads lazily. The application must
/// hold its single-instance mutex. A process-wide gate additionally serializes Core callers.
/// No resource is opened or executed. Import is detached and never writes the live vault.
/// </summary>
public sealed class VaultStore
{
    public const int MaxUtf8Bytes = 32 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        WriteIndented = true
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private Snapshot? loadedMain;
    private Snapshot? loadedBackup;
    private long loadedRevision;
    private bool recoveredFromBackup;
    private string BackupPath => DataPath + ".bak";

    public string DirectoryPath { get; }
    public string DataPath { get; }
    public bool IsReadOnly { get; private set; }
    public string? RecoveryNotice { get; private set; }

    public VaultStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory);
        DataPath = Path.Combine(DirectoryPath, "vault.json");
    }

    public VaultDocument Load()
    {
        lock (Gate)
        {
            IsReadOnly = true;
            RecoveryNotice = null;
            recoveredFromBackup = false;
            loadedMain = null;
            try { loadedMain = Retry(() => ReadSnapshot(DataPath)); }
            catch (InvalidDataException ex) { RecoveryNotice = "数据文件无法安全加载，已禁止保存。" + ex.Message; throw; }
            loadedBackup = null;
            if (loadedMain.Document is { } main)
            {
                loadedRevision = main.Revision;
                IsReadOnly = false;
                return main;
            }
            try { loadedBackup = Retry(() => ReadSnapshot(BackupPath)); }
            catch (InvalidDataException ex) { RecoveryNotice = "备份文件无法安全加载，已禁止保存。" + ex.Message; throw; }
            IsReadOnly = false;
            if (!loadedMain.Exists && !loadedBackup.Exists)
            {
                loadedRevision = 0;
                return new VaultDocument();
            }

            var notices = new List<string>();
            foreach (var entry in new[] { (DataPath, loadedMain), (BackupPath, loadedBackup) })
            {
                if (entry.Item2.Exists && entry.Item2.Document is null)
                {
                    try { notices.Add($"损坏文件已原样保留至 {PreserveCorrupt(entry.Item1, entry.Item2)}。"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        IsReadOnly = true;
                        notices.Add($"无法保留损坏文件，当前数据已设为只读：{ex.Message}");
                    }
                }
            }
            if (loadedBackup.Document is { } backup)
            {
                loadedRevision = backup.Revision;
                recoveredFromBackup = true;
                notices.Insert(0, "已载入最近一次有效备份。主文件尚未覆盖，请检查内容后再保存。");
                RecoveryNotice = string.Join(" ", notices);
                return backup;
            }
            IsReadOnly = true;
            loadedRevision = 0;
            notices.Insert(0, "数据文件与备份均无法读取。当前显示空白只读文档，原文件未被修改。");
            RecoveryNotice = string.Join(" ", notices);
            return new VaultDocument();
        }
    }

    public void Save(VaultDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (Gate)
        {
            if (loadedMain is null) Load();
            if (IsReadOnly) throw new InvalidOperationException("数据恢复失败，当前为只读状态；不会覆盖原文件。");
            // Work on a detached copy so failed writes never advance caller metadata.
            var candidate = Clone(document);
            if (loadedRevision == long.MaxValue) throw new InvalidOperationException("The vault revision is exhausted.");
            candidate.Revision = loadedRevision + 1;
            candidate.UpdatedAt = DateTimeOffset.UtcNow;
            var bytes = SerializeValidated(candidate);
            var nextSnapshot = new Snapshot(true, Hash(bytes), candidate, null);
            Directory.CreateDirectory(DirectoryPath);
            Retry(() =>
            {
                var current = ReadSnapshot(DataPath);
                RequireUnchanged(loadedMain!, current);
                if (!recoveredFromBackup && current.Document is { } currentDoc && currentDoc.Revision != loadedRevision)
                    throw new InvalidOperationException("The vault revision changed. Reload before saving.");
                if (recoveredFromBackup)
                {
                    var backup = ReadSnapshot(BackupPath);
                    RequireUnchanged(loadedBackup!, backup);
                    if (backup.Document?.Revision != loadedRevision)
                        throw new InvalidOperationException("The recovery backup changed. Reload before saving.");
                }
                else
                {
                    var backup = ReadSnapshot(BackupPath);
                    if (backup.Exists && backup.Document is null) PreserveCorrupt(BackupPath, backup);
                }
                // Never rotate corrupt main bytes over a good recovery backup.
                AtomicWrite(DataPath, bytes, current.Exists, recoveredFromBackup ? null : BackupPath);
                return true;
            });
            loadedMain = nextSnapshot;
            loadedBackup = null;
            loadedRevision = candidate.Revision;
            recoveredFromBackup = false;
            document.Revision = candidate.Revision;
            document.UpdatedAt = candidate.UpdatedAt;
        }
    }

    public void Export(string path, VaultDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (Gate)
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, DataPath, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(DataPath + ".", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Export cannot overwrite the live vault, its backups or recovery evidence. Use Save for the live vault.");
            var bytes = SerializeValidated(document);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            Retry(() =>
            {
                var exists = FileExistsStrict(fullPath);
                AtomicWrite(fullPath, bytes, exists, null);
                return true;
            });
        }
    }

    public VaultDocument Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var snapshot = Retry(() => ReadSnapshot(Path.GetFullPath(path)));
        if (!snapshot.Exists) throw new FileNotFoundException("Import file not found.", path);
        return snapshot.Document ?? throw new InvalidDataException(snapshot.Error ?? "Invalid vault document.");
    }

    public static VaultDocument Clone(VaultDocument document) => Parse(SerializeValidated(document));

    /// <summary>Throws InvalidDataException; never sanitizes, trims, truncates or mutates input.</summary>
    public static void Validate(VaultDocument document) => _ = SerializeValidated(document);

    private static byte[] SerializeValidated(VaultDocument document)
    {
        ValidateFields(document);
        try
        {
            // Bounded stream prevents a large valid object graph from allocating unbounded JSON.
            using var stream = new BoundedStream();
            JsonSerializer.Serialize(stream, document, JsonOptions);
            return stream.ToArray();
        }
        catch (JsonException ex) { throw new InvalidDataException("The document cannot be serialized safely.", ex); }
    }

    private static VaultDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaxUtf8Bytes) throw new InvalidDataException("Vault exceeds the 32 MiB UTF-8 limit.");
        try
        {
            // Reject malformed UTF-8 rather than substituting replacement characters.
            _ = StrictUtf8.GetCharCount(bytes);
            var result = JsonSerializer.Deserialize<VaultDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Vault root must be an object.");
            ValidateFields(result);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("数据文件格式无效、不完整或版本不受支持。", ex); }
    }

    private static void ValidateFields(VaultDocument document)
    {
        if (document is null) throw new InvalidDataException("Document is required.");
        Require(document.Version == 1, "Only vault version 1 is supported.");
        Require(document.Revision >= 0, "Revision cannot be negative.");
        Date(document.UpdatedAt, "Vault updatedAt");
        Require(document.Cards is not null && document.Cards.Count <= 5000, "Vault must contain at most 5000 cards.");
        Require(document.Settings is not null, "Settings are required.");
        var settings = document.Settings!;
        Require(settings.FocusMinutes is >= 1 and <= 1440, "Focus minutes must be between 1 and 1440.");
        Text(settings.CaptureHotkey, 100, "Capture hotkey", false);
        Text(settings.ResumeHotkey, 100, "Resume hotkey", false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in document.Cards!)
        {
            Require(card is not null, "Cards cannot contain null.");
            Id(card!.Id, ids, "Card ID");
            Text(card.Title, 200, "Title", false);
            Text(card.NextAction, 2000, "Next action", true);
            Text(card.Context, 50000, "Context", true);
            Require(card.Status is "parked" or "active" or "done", "Card status must be parked, active, or done.");
            Require(card.ResumeCount >= 0, "Resume count cannot be negative.");
            Date(card.CreatedAt, "Created at"); Date(card.UpdatedAt, "Updated at");
            Require(card.UpdatedAt >= card.CreatedAt, "Card updatedAt cannot precede createdAt.");
            OptionalDate(card.SnoozedUntil, "Snoozed until");
            OptionalDate(card.CompletedAt, "Completed at"); OptionalDate(card.LastResumedAt, "Last resumed at");
            Require(card.CompletedAt is null || card.CompletedAt >= card.CreatedAt, "Completion cannot precede creation.");
            Require(card.LastResumedAt is null || card.LastResumedAt >= card.CreatedAt, "Resume cannot precede creation.");
            Require(card.Resources is not null && card.Resources.Count <= 100, "Each card may contain at most 100 resources.");
            foreach (var resource in card.Resources!)
            {
                Require(resource is not null, "Resources cannot contain null.");
                Id(resource!.Id, ids, "Resource ID");
                Text(resource.Label, 2000, "Resource label", false);
                Text(resource.Target, 32767, "Resource target", false);
                if (resource.Kind == "url")
                {
                    Require(Uri.TryCreate(resource.Target, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host) &&
                        !resource.Target.Any(char.IsWhiteSpace) && !resource.Target.Contains('\\'),
                        "Only absolute HTTP/HTTPS URLs are supported.");
                }
                else
                {
                    Require(resource.Kind is "file" or "folder", "Resource kind must be file, folder, or url.");
                    ValidateLocalPath(resource.Target);
                }
            }
            Require(card.Checkpoints is not null && card.Checkpoints.Count <= 5000, "Each card may contain at most 5000 checkpoints.");
            foreach (var checkpoint in card.Checkpoints!)
            {
                Require(checkpoint is not null, "Checkpoints cannot contain null.");
                Date(checkpoint!.At, "Checkpoint date"); Text(checkpoint.Text, 50000, "Checkpoint text", true);
            }
        }
    }

    private static void ValidateLocalPath(string path)
    {
        Require(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/',
            "File/folder targets must be drive-rooted local Windows paths; UNC/device/relative paths are not supported.");
        Require(!path.AsSpan(2).ContainsAny(':', '*', '?') && !path.Any(c => c is '"' or '<' or '>' or '|'),
            "Resource paths cannot contain wildcard, device, or alternate-data-stream syntax.");
        foreach (var segment in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            Require(segment is not "." and not ".." && !segment.EndsWith(' ') && !segment.EndsWith('.'),
                "Resource paths cannot contain ambiguous dot/space segments.");
            var name = segment.Split('.')[0].ToUpperInvariant();
            Require(name is not "CON" and not "PRN" and not "AUX" and not "NUL" and not "CLOCK$" &&
                !(name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && "123456789¹²³".Contains(name[3])),
                "Windows device names are not valid resource targets.");
        }
    }

    private static void Id(string value, HashSet<string> ids, string field)
    {
        Text(value, 128, field, false);
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !ids.Add(value))
            throw new InvalidDataException($"{field} must be nonblank and globally unique.");
    }
    private static void Text(string? value, int max, string field, bool multiline)
    {
        // Construct contextual messages only on failure: per-character interpolation creates
        // hundreds of MiB of short-lived garbage when validating a large, valid vault.
        if (value is null || value.Length > max)
            throw new InvalidDataException($"{field} is required and may contain at most {max} UTF-16 characters.");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')) ||
                c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
                '\u2066' or '\u2067' or '\u2068' or '\u2069')
                throw new InvalidDataException($"{field} contains a forbidden control character.");
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    throw new InvalidDataException($"{field} contains invalid Unicode.");
                i++;
            }
            else if (char.IsLowSurrogate(c))
                throw new InvalidDataException($"{field} contains invalid Unicode.");
        }
    }
    private static void Date(DateTimeOffset value, string field)
    {
        if (value.Year is < 1970 or > 9998)
            throw new InvalidDataException($"{field} must be between years 1970 and 9998.");
    }
    private static void OptionalDate(DateTimeOffset? value, string field) { if (value.HasValue) Date(value.Value, field); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record Snapshot(bool Exists, string? Fingerprint, VaultDocument? Document, string? Error);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Snapshot ReadSnapshot(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Check metadata before hashing or reading: even a sparse multi-GB input must fail fast.
            if (stream.Length > MaxUtf8Bytes)
                throw new InvalidDataException("数据文件超过 32 MiB 上限；未读取、复制或修改原文件。");
            var fingerprint = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            try { return new(true, fingerprint, Parse(bytes), null); }
            catch (InvalidDataException ex) { return new(true, fingerprint, null, ex.Message); }
        }
        catch (FileNotFoundException) { return new(false, null, null, null); }
        catch (DirectoryNotFoundException) { return new(false, null, null, null); }
    }
    private static void RequireUnchanged(Snapshot expected, Snapshot actual)
    {
        if (expected.Exists != actual.Exists || expected.Fingerprint != actual.Fingerprint)
            throw new InvalidOperationException("磁盘数据已被修改。请重新载入并核对更改后再保存。");
    }
    private static string PreserveCorrupt(string path, Snapshot snapshot)
    {
        var copy = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaxUtf8Bytes)
            throw new IOException("损坏文件已超过 32 MiB 上限；为保留原文件，已停止自动恢复。");
        if (Convert.ToHexString(SHA256.HashData(source)) != snapshot.Fingerprint)
            throw new IOException("Corrupt file changed during recovery. Reload before continuing.");
        source.Position = 0;
        using var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(true);
        return copy;
    }
    private static bool FileExistsStrict(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static void AtomicWrite(string path, byte[] bytes, bool exists, string? backup)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (exists) File.Replace(temp, path, backup, ignoreMetadataErrors: false);
            else File.Move(temp, path, overwrite: false);
        }
        finally
        {
            // Cleanup is best effort only; do not turn a committed save into an apparent failure.
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static T Retry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return operation(); }
            catch (IOException) when (attempt < 3)
            { Thread.Sleep(40 * attempt); }
        }
    }
    private sealed class BoundedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { EnsureLimit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { EnsureLimit(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { EnsureLimit(1); base.WriteByte(value); }
        private void EnsureLimit(int count)
        { if (Position + count > MaxUtf8Bytes) throw new InvalidDataException("Vault exceeds the 32 MiB UTF-8 limit."); }
    }
}
