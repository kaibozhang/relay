using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Desktop.Services;

/// <summary>An immutable snapshot. Only FocusSessionStore changes the persisted timeline.</summary>
public sealed class FocusSession
{
    public string Id { get; init; } = "";
    public string CardId { get; init; } = "";
    public int Minutes { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public double RemainingSeconds { get; init; }
    public bool Paused { get; init; }
    public bool CompletionPending { get; init; }
}

/// <summary>
/// Single-owner, restart-safe focus timeline, independent of the vault's card schema.
/// Callers must serialize access (normally on the dispatcher). Disk errors propagate;
/// Current changes only after the live file is committed. Warning retains recovery notices.
/// </summary>
public sealed class FocusSessionStore
{
    private const int MaxFileBytes = 64 * 1024;
    private static readonly DateTimeOffset EarliestDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LatestDate = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 4
    };
    private static readonly HashSet<string> PropertyNames = new(StringComparer.Ordinal)
    {
        "id", "cardId", "minutes", "startedAt", "deadline", "remainingSeconds", "paused", "completionPending"
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly string _backupPath;
    private bool _loaded;

    public FocusSessionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "focus.json");
        _backupPath = _path + ".bak";
    }

    public FocusSession? Current { get; private set; }
    public string? Warning { get; private set; }

    /// <summary>
    /// Prefer live data (including a null tombstone); fall back to the backup only if
    /// live data is missing or invalid. Invalid bytes are copied, never discarded.
    /// An unreadable file or failed evidence copy throws rather than authorizing an overwrite.
    /// </summary>
    public void Load()
    {
        try
        {
            var live = ReadFile(_path);
            var backup = ReadFile(_backupPath);
            PreserveInvalid(_path, live);
            PreserveInvalid(_backupPath, backup);
            FocusSession? loaded = live.Valid ? live.Session : backup.Valid ? backup.Session : null;
            if (!live.Valid && backup.Valid)
                AddWarning("Focus timeline recovered from focus.json.bak; review the restored session.");
            else if (!live.Valid && !backup.Valid && (live.Exists || backup.Exists))
                AddWarning("No valid focus timeline could be recovered. Corrupt files were preserved.");
            Current = loaded;
            _loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddWarning("Cannot load focus timeline: " + ex.Message);
            throw;
        }
    }

    public void Start(string cardId, int minutes, DateTimeOffset? now = null)
    {
        ValidateCardId(cardId);
        if (minutes is < 1 or > 180) throw new ArgumentOutOfRangeException(nameof(minutes), "Focus duration must be 1..180 minutes.");
        DateTimeOffset started = now ?? DateTimeOffset.UtcNow;
        ValidateDate(started);
        var next = new FocusSession
        {
            Id = Guid.NewGuid().ToString("N"), CardId = cardId, Minutes = minutes,
            StartedAt = started, Deadline = started.AddMinutes(minutes), RemainingSeconds = minutes * 60
        };
        EnsureLoaded();
        if (Current?.CompletionPending == true)
            throw new InvalidOperationException("Acknowledge or cancel the pending completion before starting another session.");
        Save(next);
    }

    public void Pause(DateTimeOffset? now = null)
    {
        EnsureLoaded();
        if (Current is null || Current.Paused || Current.CompletionPending) return;
        double seconds = Remaining(now).TotalSeconds;
        if (seconds <= 0) { MarkCompletionPending(now); return; }
        Save(Copy(Current, null, seconds, paused: true, pending: false));
    }

    public void Resume(DateTimeOffset? now = null)
    {
        EnsureLoaded();
        if (Current is null || !Current.Paused || Current.CompletionPending) return;
        DateTimeOffset resumed = now ?? DateTimeOffset.UtcNow;
        ValidateDate(resumed);
        Save(Copy(Current, resumed.AddSeconds(Current.RemainingSeconds), Current.RemainingSeconds, paused: false, pending: false));
    }

    public void Cancel()
    {
        EnsureLoaded();
        // Even an empty timeline gets a durable tombstone, preventing backup resurrection.
        Save(null);
    }

    public TimeSpan Remaining(DateTimeOffset? now = null)
    {
        EnsureLoaded();
        if (Current is null || Current.CompletionPending) return TimeSpan.Zero;
        double seconds = Current.Paused ? Current.RemainingSeconds : (Current.Deadline!.Value - (now ?? DateTimeOffset.UtcNow)).TotalSeconds;
        // A backwards wall-clock adjustment must not extend the original duration.
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Current.RemainingSeconds));
    }

    public bool IsDue(DateTimeOffset? now = null)
    {
        EnsureLoaded();
        return Current is not null && (Current.CompletionPending || (!Current.Paused && Remaining(now) <= TimeSpan.Zero));
    }

    /// <summary>
    /// Persist intent BEFORE saving a checkpoint containing Current.Id. On restart,
    /// find that marker first, create it only if absent, then AcknowledgeCompletion.
    /// </summary>
    public void MarkCompletionPending(DateTimeOffset? now = null)
    {
        EnsureLoaded();
        if (Current is null || Current.CompletionPending) return;
        if (!IsDue(now)) throw new InvalidOperationException("The focus session is not due yet.");
        Save(Copy(Current, Current.Deadline, 0, paused: false, pending: true));
    }

    public void AcknowledgeCompletion()
    {
        EnsureLoaded();
        if (Current is null) return;
        if (!Current.CompletionPending)
            throw new InvalidOperationException("Mark completion pending before acknowledging it.");
        Save(null);
    }

    private static FocusSession Copy(FocusSession session, DateTimeOffset? deadline, double seconds, bool paused, bool pending) => new()
    {
        Id = session.Id, CardId = session.CardId, Minutes = session.Minutes, StartedAt = session.StartedAt,
        Deadline = deadline, RemainingSeconds = seconds, Paused = paused, CompletionPending = pending
    };

    private void EnsureLoaded() { if (!_loaded) Load(); }

    private void Save(FocusSession? next)
    {
        Validate(next);
        try
        {
            Directory.CreateDirectory(_directory);
            // Recheck external changes before replacing either owned path. Never erase bad bytes.
            var live = ReadFile(_path);
            PreserveInvalid(_path, live);
            PreserveInvalid(_backupPath, ReadFile(_backupPath));
            // After fallback recovery, establish the recovered committed state as live
            // before touching its only valid backup. A later failed clear cannot lose it.
            if (!live.Valid) AtomicWrite(_path, JsonSerializer.SerializeToUtf8Bytes(Current, JsonOptions));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            // Backup is the previous committed state; clearing writes a backup tombstone FIRST.
            // If the live commit fails, its valid old state remains authoritative on restart.
            byte[] backupBytes = JsonSerializer.SerializeToUtf8Bytes(next is null ? null : Current, JsonOptions);
            AtomicWrite(_backupPath, backupBytes);
            AtomicWrite(_path, bytes);
            Current = next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddWarning("Cannot save focus timeline; the in-memory session is unchanged: " + ex.Message);
            throw;
        }
    }

    private static void AtomicWrite(string destination, byte[] bytes)
    {
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Rename-overwrite is atomic on the same volume. No delete-then-move gap.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // Only our uniquely named staging file is eligible for cleanup.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ReadResult(bool Exists, bool Valid, FocusSession? Session, string? Error);

    private static ReadResult ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) return new(true, false, null, "file exceeds 64 KiB");
            // Bounded allocation even if an external writer grows the file after the length check.
            byte[] bytes = new byte[MaxFileBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxFileBytes) return new(true, false, null, "file exceeds 64 KiB");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null) return new(true, true, null, null);
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a session object or null.");
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!PropertyNames.Contains(property.Name) || !found.Add(property.Name))
                    throw new JsonException("Unknown or duplicate focus property: " + property.Name);
            }
            if (found.Count != PropertyNames.Count) throw new JsonException("Missing required focus properties.");
            FocusSession? session = root.Deserialize<FocusSession>(JsonOptions);
            Validate(session);
            return new(true, true, session, null);
        }
        catch (FileNotFoundException) { return new(false, false, null, null); }
        catch (DirectoryNotFoundException) { return new(false, false, null, null); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return new(true, false, null, ex.Message);
        }
    }

    private void PreserveInvalid(string path, ReadResult result)
    {
        if (!result.Exists || result.Valid) return;
        string evidence = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N");
        File.Copy(path, evidence, overwrite: false);
        AddWarning("Invalid " + Path.GetFileName(path) + " (" + result.Error + "). Original bytes preserved as " + Path.GetFileName(evidence) + ".");
    }

    private void AddWarning(string message) => Warning = Warning is null ? message : Warning + Environment.NewLine + message;

    private static void Validate(FocusSession? session)
    {
        if (session is null) return;
        if (!Guid.TryParseExact(session.Id, "N", out Guid id) || id == Guid.Empty)
            throw new InvalidDataException("Focus id must be a nonempty compact GUID.");
        ValidateCardId(session.CardId);
        if (session.Minutes is < 1 or > 180) throw new InvalidDataException("Focus minutes must be 1..180.");
        ValidateDate(session.StartedAt);
        if (!double.IsFinite(session.RemainingSeconds) || session.RemainingSeconds < 0 || session.RemainingSeconds > session.Minutes * 60)
            throw new InvalidDataException("Invalid remaining focus seconds.");
        if (session.Deadline is DateTimeOffset deadline)
        {
            ValidateDate(deadline);
            if (deadline <= session.StartedAt) throw new InvalidDataException("Focus deadline must follow its start.");
        }
        if (session.Paused)
        {
            if (session.Deadline is not null || session.RemainingSeconds <= 0 || session.CompletionPending)
                throw new InvalidDataException("Inconsistent paused focus session.");
        }
        else if (session.Deadline is null || (session.CompletionPending ? session.RemainingSeconds != 0 : session.RemainingSeconds <= 0))
            throw new InvalidDataException("Inconsistent running or pending focus session.");
    }

    private static void ValidateCardId(string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId) || cardId.Length > 128)
            throw new ArgumentException("Card id must contain 1..128 characters.", nameof(cardId));
        foreach (char character in cardId)
            if (char.IsControl(character)) throw new ArgumentException("Card id cannot contain control characters.", nameof(cardId));
    }

    private static void ValidateDate(DateTimeOffset date)
    {
        if (date < EarliestDate || date >= LatestDate)
            throw new ArgumentOutOfRangeException(nameof(date), "Focus dates must be between 2000 and 2200 UTC.");
    }
}
