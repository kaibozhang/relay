using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core;

namespace Relay.Desktop.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CaptureDraft
{
    [JsonPropertyName("version"), JsonRequired] public int Version { get; set; } = 1;
    [JsonPropertyName("card"), JsonRequired] public RelayCard Card { get; set; } = new();
    [JsonPropertyName("savedAt"), JsonRequired] public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Detached new-capture drafts, never vault edits or progress. Resource paths are data only.
/// Callers must hold the application's single-instance mutex; the gate serializes in-process callers.
/// Reparse checks reject existing links, not adversarial concurrent filesystem substitution.
/// Save/Delete throw on failure; List preserves unreadable entries and reports them via Warning.
/// </summary>
public sealed class CaptureDraftStore
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxDrafts = 50;
    private const int MaxEntries = 200;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        WriteIndented = true
    };

    public string DirectoryPath { get; }
    public string? Warning { get; private set; }

    public CaptureDraftStore(string vaultDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        DirectoryPath = Path.Combine(Path.GetFullPath(vaultDirectory), "drafts");
    }

    public IReadOnlyList<CaptureDraft> List()
    {
        lock (Gate)
        {
            Warning = null;
            var drafts = new List<CaptureDraft>();
            var notices = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!CheckDirectory()) return drafts;
                // Bound *all* directory entries, including junk and subdirectories, not just matches.
                using var entries = Directory.EnumerateFileSystemEntries(DirectoryPath).GetEnumerator();
                var visited = 0;
                while (visited < MaxEntries && entries.MoveNext())
                {
                    visited++;
                    var path = entries.Current;
                    if (!IsJson(path)) continue;
                    try
                    {
                        var id = Path.GetFileNameWithoutExtension(path);
                        ValidateId(id);
                        drafts.Add(ReadDraft(path, id));
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        notices.Add("部分草稿文件无法安全读取，已原样保留，未显示在列表中。");
                    }
                }
                // Conservatively warn at the bound without requesting a 201st entry.
                if (visited == MaxEntries)
                    notices.Add("草稿目录扫描已达到 200 项安全上限，其余文件可能未显示，原文件均未删除。");
                if (drafts.Count > MaxDrafts)
                    notices.Add("仅显示已扫描文件中最近保存的 50 份有效草稿，其余文件已原样保留。");
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                notices.Add("无法安全读取草稿目录，现有文件均未修改。");
            }
            Warning = notices.Count == 0 ? null : string.Join(" ", notices);
            return drafts.OrderByDescending(d => d.SavedAt).ThenBy(d => d.Card.Id, StringComparer.Ordinal)
                .Take(MaxDrafts).ToArray();
        }
    }

    public void Save(RelayCard card)
    {
        lock (Gate)
        {
            Warning = null;
            string? temp = null;
            var ownsTemp = false;
            try
            {
                ArgumentNullException.ThrowIfNull(card);
                ValidateId(card.Id);
                ValidateCapture(card);
                // Core validates the original before JSON cloning, including invalid UTF-16.
                // Neither validation nor cloning mutates caller-owned lists or model values.
                var clone = VaultStore.Clone(new VaultDocument { Cards = [card] }).Cards[0];
                using var buffer = new BoundedStream();
                JsonSerializer.Serialize(buffer, new CaptureDraft { Card = clone }, JsonOptions);
                var bytes = buffer.ToArray();
                CheckDirectory();
                var path = DraftPath(card.Id);
                var exists = CheckFile(path);
                if (exists) _ = ReadDraft(path, card.Id); // Never overwrite corrupt evidence implicitly.
                else CheckCapacity();
                Directory.CreateDirectory(DirectoryPath);
                CheckDirectory();
                temp = Path.Combine(DirectoryPath, "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    ownsTemp = true;
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                CheckDirectory();
                if (CheckFile(path) != exists)
                    throw new IOException("保存过程中草稿文件发生变化，请刷新草稿列表后重试。");
                // Same-directory atomic replacement; deliberately no backup or recovery resurrection.
                if (exists) File.Replace(temp, path, null);
                else File.Move(temp, path);
                temp = null;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Warning = "草稿未保存。" + ex.Message;
                if (ex is JsonException) throw new InvalidDataException("现有草稿格式无效，已原样保留，未覆盖。", ex);
                throw;
            }
            finally
            {
                if (temp is not null && ownsTemp)
                {
                    try
                    {
                        if (CheckDirectory() && CheckFile(temp)) File.Delete(temp);
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        Warning = (Warning ?? "草稿保存失败。") + " 无法安全清理本次保存的临时文件。";
                    }
                }
            }
        }
    }

    public void Delete(string id)
    {
        lock (Gate)
        {
            Warning = null;
            try
            {
                ValidateId(id);
                if (!CheckDirectory()) return;
                var path = DraftPath(id);
                if (CheckFile(path)) File.Delete(path);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Warning = "草稿未删除。" + ex.Message;
                throw;
            }
        }
    }

    private string DraftPath(string id) => Path.Combine(DirectoryPath, id + ".json");
    private static bool IsJson(string path) => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private void CheckCapacity()
    {
        if (!CheckDirectory()) return;
        using var entries = Directory.EnumerateFileSystemEntries(DirectoryPath).GetEnumerator();
        var count = 0;
        var visited = 0;
        while (visited < MaxEntries && entries.MoveNext())
        {
            visited++;
            // Invalid names and corrupt files also occupy capacity: never silently remove them.
            if (IsJson(entries.Current) && ++count >= MaxDrafts)
                throw new InvalidDataException("最多允许 50 份草稿文件，请先删除不需要的草稿再新建。");
        }
        if (visited == MaxEntries)
            throw new InvalidDataException("草稿目录中的文件过多，无法安全确认剩余容量，请先检查目录。");
    }

    private CaptureDraft ReadDraft(string path, string id)
    {
        CheckDirectory();
        if (!CheckFile(path)) throw new FileNotFoundException("草稿文件已不存在。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("草稿文件超过 1 MiB 安全上限。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("读取过程中草稿文件发生变化。");
        _ = StrictUtf8.GetCharCount(bytes);
        var draft = JsonSerializer.Deserialize<CaptureDraft>(bytes, JsonOptions)
            ?? throw new InvalidDataException("草稿内容必须是完整的 JSON 对象。");
        if (draft.Version != 1 || draft.Card is null || draft.SavedAt.Year is < 1970 or > 9998)
            throw new InvalidDataException("草稿元数据无效或版本不受支持。");
        ValidateId(draft.Card.Id);
        if (!string.Equals(id, draft.Card.Id, StringComparison.Ordinal))
            throw new InvalidDataException("草稿文件名与卡片标识不一致。");
        ValidateCapture(draft.Card);
        return draft;
    }

    private static void ValidateCapture(RelayCard card)
    {
        VaultStore.Validate(new VaultDocument { Cards = [card] });
        if (card.Status != "parked" || card.ResumeCount != 0 || card.LastResumedAt is not null ||
            card.CompletedAt is not null || card.SnoozedUntil is not null || card.Checkpoints.Count != 0)
            throw new InvalidDataException("只能保存新建捕获草稿，不能包含进度、恢复或完成记录。");
    }

    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 32 || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("草稿标识必须是 32 位十六进制 GUID，不能包含路径。");
    }

    // Check every existing ancestor as well as drafts itself; Directory.Exists alone hides access errors
    // and misses dangling symbolic links. Never create directories from a read/delete operation.
    private bool CheckDirectory()
    {
        var missing = false;
        for (var current = new DirectoryInfo(DirectoryPath); current is not null; current = current.Parent)
        {
            var attributes = Attributes(current.FullName);
            if (attributes is null) { missing = true; continue; }
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0 ||
                (attributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("草稿目录及其父目录必须是真实目录，不能是符号链接或重解析点。");
        }
        return !missing;
    }

    private static bool CheckFile(string path)
    {
        var attributes = Attributes(path);
        if (attributes is null) return false;
        if ((attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("草稿路径必须是普通文件，不能是目录、符号链接或重解析点。");
        return true;
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private sealed class BoundedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckSize(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckSize(buffer.Length);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            CheckSize(1);
            base.WriteByte(value);
        }
        private void CheckSize(int count)
        {
            if (Position + count > MaxBytes) throw new InvalidDataException("草稿文件超过 1 MiB 安全上限。");
        }
    }
}
