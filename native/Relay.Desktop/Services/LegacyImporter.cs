using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Relay.Core;

namespace Relay.Desktop.Services;

/// <summary>Detached candidates only. Counts refer to source records, not continuation cards.</summary>
public sealed class MigrationPreview
{
    /// <summary>Active source notes; excludes trash and counts each split note once.</summary>
    public int NoteCount { get; init; }
    /// <summary>All validated source tasks, including completed tasks.</summary>
    public int TaskCount { get; init; }
    /// <summary>Validated source notes with non-null deletedAt; never emitted as cards.</summary>
    public int SkippedTrashCount { get; init; }
    /// <summary>Ordered detached candidates, including continuations; use Count for capacity checks.</summary>
    public List<RelayCard> Cards { get; init; } = [];
    public string Summary { get; init; } = "";
}

/// <summary>
/// Reads only the explicitly selected Lumen v1 export. Never reads browser storage, writes a
/// vault, follows embedded links, renders HTML, or attaches resources. The caller must confirm
/// the preview, check existing cards + preview cards <= 5000, then save one atomic candidate.
/// </summary>
public static class LegacyImporter
{
    public const int MaxInputBytes = 20 * 1024 * 1024;
    public const int MaxOutputCards = 5000;
    private const int MaxContext = 50000;
    private const int BodyChunkLimit = 45000; // UTF-16 units: also bounds Unicode scalar count.
    private const string BodyMarker = "\n--- Lumen 正文 ---\n";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] NoteFields = ["id", "title", "body", "tags", "pinned", "createdAt", "updatedAt", "deletedAt"];
    private static readonly string[] TaskFields = ["id", "title", "done", "priority", "dueDate", "createdAt", "completedAt"];

    public static MigrationPreview Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        // FileShare.Read denies concurrent writers on Windows. Check the opened handle's size
        // before allocating or parsing, then detect growth even on platforms with advisory locks.
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxInputBytes)
            throw Invalid("文件", "超过 20 MiB；请先保留原始备份，再在备份副本中整理内容，或分批保存结构完整的有效备份后重试");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw Invalid("文件", "读取时发生变化；请重新选择稳定的导出文件");

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            // A UTF-8 BOM is permitted, and counts toward the input byte limit.
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                json = json[3..];
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            try { ValidateTree(root); }
            catch (InvalidOperationException ex)
            { throw new InvalidDataException("JSON 字段名或文字包含无效 Unicode；请修复后重新导出。", ex); }
            Require(root.ValueKind == JsonValueKind.Object, "根节点", "应为 JSON 对象");
            var version = Required(root, "version", "根节点");
            Require(version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1,
                "version", "仅支持 Lumen 版本 1 导出文件");
            var notesJson = Array(Required(root, "notes", "根节点"), 10000, "notes");
            var tasksJson = Array(Required(root, "tasks", "根节点"), 10000, "tasks");
            if (root.TryGetProperty("updatedAt", out var updated)) Iso(updated, "updatedAt", false);

            // Complete validation, including trash, before creating any Relay candidate.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var notes = new List<LegacyNote>();
            var tasks = new List<LegacyTask>();
            var index = 0;
            foreach (var note in notesJson.EnumerateArray()) notes.Add(ReadNote(note, $"notes[{index++}]", ids));
            index = 0;
            foreach (var task in tasksJson.EnumerateArray()) tasks.Add(ReadTask(task, $"tasks[{index++}]", ids));

            var now = DateTimeOffset.UtcNow;
            var cards = new List<RelayCard>();
            int activeNotes = 0, trash = 0, shortened = 0, splitNotes = 0, continuationCards = 0;
            foreach (var note in notes)
            {
                if (note.DeletedAt is not null) { trash++; continue; }
                activeNotes++;
                if (note.Title.Length > 200) shortened++;
                var metadata = $"来源：Lumen v1 笔记\n原始 ID：{note.Id}\n完整原始标题：{note.Title}\n原创建时间：{note.CreatedAt}\n原更新时间：{note.UpdatedAt}\n标签：\n"
                    + string.Join("\n", note.Tags.Select(tag => "- " + tag));
                // Reserve space for the continuation label before computing lossless chunks.
                var chunkSize = Math.Min(BodyChunkLimit, MaxContext - metadata.Length - BodyMarker.Length - 80);
                Require(chunkSize >= 2, "笔记", "元数据过长；请减少标签后重新导出");
                var chunks = Split(note.Body, chunkSize);
                if (chunks.Count > 1) { splitNotes++; continuationCards += chunks.Count - 1; }
                for (var part = 0; part < chunks.Count; part++)
                {
                    var suffix = chunks.Count > 1 ? $"（续页 {part + 1}/{chunks.Count}）" : "";
                    var title = DisplayTitle(note.Title, "未命名笔记", suffix);
                    var context = metadata + $"\n正文分段：{part + 1}/{chunks.Count}" + BodyMarker + chunks[part];
                    Add(cards, NewCard(title, "回看这条笔记，写下具体的下一步", context, note.Pinned, false, now));
                }
            }
            foreach (var task in tasks)
            {
                if (task.Title.Length > 200) shortened++;
                var context = $"来源：Lumen v1 任务\n原始 ID：{task.Id}\n完整原始标题：{task.Title}\n优先级：{task.Priority}\n原创建时间：{task.CreatedAt}\n原完成时间：{task.CompletedAt ?? "未完成"}\n原截止日期：{(task.DueDate.Length == 0 ? "未设置" : task.DueDate)}（仅文字，不创建提醒）";
                Add(cards, NewCard(DisplayTitle(task.Title, "未命名任务", ""), task.Title, context, false, task.Done, now));
            }

            // Core validates the detached result, including its serialized byte budget; no writes.
            try { VaultStore.Validate(new VaultDocument { Cards = cards, UpdatedAt = now }); }
            catch (InvalidDataException ex)
            { throw new InvalidDataException("转换结果不符合 Relay 数据限制（例如标题含换行、正文含不支持的控制字符或总量超过 32 MiB）。请检查原始文字或减少本次内容、分批迁移；不会自动删改正文。原文件未修改。详情：" + ex.Message, ex); }

            return new MigrationPreview
            {
                NoteCount = activeNotes, TaskCount = tasks.Count, SkippedTrashCount = trash, Cards = cards,
                Summary = $"将导入 {activeNotes} 条笔记、{tasks.Count} 条任务，共 {cards.Count} 张卡片；跳过回收站 {trash} 条笔记。\n"
                    + $"{shortened} 个长标题已缩短显示，完整原始标题保存在上下文；空白标题显示为“未命名笔记”。{splitNotes} 条长笔记拆为续页，增加 {continuationCards} 张卡片；正文与换行完整保留，不截断。\n"
                    + "笔记默认下一步为“回看这条笔记，写下具体的下一步”，请自行编辑；未完成任务放在暂存，已完成任务保留完成状态。\n"
                    + "sessions（专注记录）、timer（计时器）、settings（设置）及其他未知根字段忽略；不生成恢复次数、专注统计或历史检查点。图谱链接 [[...]]、HTML、网址与本地路径仅作为纯文字保留，不解析、不访问、不自动附加资源。\n"
                    + "截止日期仅保存为文字，不建立定时提醒。新卡片使用本次导入时间（含已完成卡片的完成时间），原日期保存在上下文。\n"
                    + "此处仅预览，不修改原文件或当前资料库；确认前须检查与现有卡片合计不超过 5000 张，并校验合并后的资料库大小。"
            };
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Lumen 导出文件不是有效的 UTF-8 JSON，或嵌套超过 32 层；未导入任何数据。", ex); }
    }

    private static LegacyNote ReadNote(JsonElement item, string path, HashSet<string> ids)
    {
        Fields(item, NoteFields, path);
        var id = Identifier(item.GetProperty("id"), path + ".id", ids);
        var title = Text(item.GetProperty("title"), 300, path + ".title");
        var body = Text(item.GetProperty("body"), 200000, path + ".body");
        var tags = new List<string>();
        foreach (var tag in Array(item.GetProperty("tags"), 50, path + ".tags").EnumerateArray())
            tags.Add(Text(tag, 300, path + ".tags[]", true));
        return new(id, title, body, tags, Boolean(item.GetProperty("pinned"), path + ".pinned"),
            Iso(item.GetProperty("createdAt"), path + ".createdAt", false)!,
            Iso(item.GetProperty("updatedAt"), path + ".updatedAt", false)!,
            Iso(item.GetProperty("deletedAt"), path + ".deletedAt", true));
    }

    private static LegacyTask ReadTask(JsonElement item, string path, HashSet<string> ids)
    {
        Fields(item, TaskFields, path);
        var id = Identifier(item.GetProperty("id"), path + ".id", ids);
        var title = Text(item.GetProperty("title"), 300, path + ".title", true);
        var done = Boolean(item.GetProperty("done"), path + ".done");
        var priority = Text(item.GetProperty("priority"), 6, path + ".priority");
        Require(priority is "low" or "normal" or "high", path + ".priority", "应为 low、normal 或 high");
        var due = Text(item.GetProperty("dueDate"), 10, path + ".dueDate");
        Require(due.Length == 0 || CalendarDate(due), path + ".dueDate", "应为空文字或有效的 yyyy-MM-dd 日期");
        var created = Iso(item.GetProperty("createdAt"), path + ".createdAt", false)!;
        var completed = Iso(item.GetProperty("completedAt"), path + ".completedAt", true);
        Require(done == (completed is not null), path, "完成状态与完成时间不一致");
        return new(id, title, done, priority, due, created, completed);
    }

    private static RelayCard NewCard(string title, string action, string context, bool pinned, bool done, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Title = title, NextAction = action, Context = context,
        Pinned = pinned, Status = done ? "done" : "parked", CreatedAt = now, UpdatedAt = now,
        CompletedAt = done ? now : null, Resources = [], Checkpoints = [], ResumeCount = 0,
        SnoozedUntil = null, LastResumedAt = null
    };

    private static void Add(List<RelayCard> cards, RelayCard card)
    {
        Require(cards.Count < MaxOutputCards, "转换结果", "超过 5000 张卡片（含续页）；请先保留原始备份，再整理备份副本或分批保存结构完整的有效备份，并为现有卡片预留容量");
        cards.Add(card);
    }

    private static string DisplayTitle(string original, string fallback, string suffix)
    {
        var title = string.IsNullOrWhiteSpace(original) ? fallback : original;
        return title[..SafeLength(title, 0, 200 - suffix.Length)] + suffix;
    }

    private static List<string> Split(string body, int limit)
    {
        var chunks = new List<string>();
        for (var offset = 0; offset < body.Length;)
        {
            var length = SafeLength(body, offset, limit);
            chunks.Add(body.Substring(offset, length));
            offset += length;
        }
        if (chunks.Count == 0) chunks.Add("");
        return chunks;
    }

    private static int SafeLength(string text, int start, int limit)
    {
        var length = Math.Min(text.Length - start, limit);
        if (length > 0 && start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
        return length;
    }

    // JsonDocument accepts duplicate property names by default. Check every object, including
    // ignored root fields, with ordinal decoded names (so "id" and "\u0069d" collide).
    private static void ValidateTree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "JSON 对象", "包含重复字段");
                ValidateUnicode(property.Name);
                ValidateTree(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateTree(item);
        else if (value.ValueKind == JsonValueKind.String)
        {
            try { ValidateUnicode(value.GetString()!); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("JSON 文字包含无效 Unicode；请修复后重新导出。", ex); }
        }
    }

    private static void ValidateUnicode(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            Require(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                "JSON 文字", "包含无效 Unicode 代理字符；请修复后重新导出");
            i++;
        }
    }

    private static void Fields(JsonElement value, string[] fields, string path)
    {
        Require(value.ValueKind == JsonValueKind.Object, path, "应为对象");
        foreach (var property in value.EnumerateObject())
            Require(fields.Contains(property.Name, StringComparer.Ordinal), path, "包含不支持的记录字段");
        foreach (var field in fields) _ = Required(value, field, path);
    }

    private static JsonElement Required(JsonElement value, string name, string path)
    {
        Require(value.TryGetProperty(name, out var result), path + "." + name, "缺少必要字段");
        return result;
    }

    private static JsonElement Array(JsonElement value, int max, string path)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= max, path, $"应为不超过 {max} 项的数组");
        return value;
    }

    private static string Text(JsonElement value, int max, string path, bool nonempty = false)
    {
        Require(value.ValueKind == JsonValueKind.String, path, "应为文字");
        var text = value.GetString()!;
        Require(text.Length <= max && (!nonempty || !string.IsNullOrWhiteSpace(text)), path, $"文字超过 {max} 字符上限或必要内容为空");
        return text;
    }

    private static string Identifier(JsonElement value, string path, HashSet<string> ids)
    {
        var id = Text(value, 128, path, true);
        Require(ids.Add(id), path, "存在重复 ID");
        return id;
    }

    private static bool Boolean(JsonElement value, string path)
    {
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path, "应为布尔值，不能用文字或数字代替");
        return value.GetBoolean();
    }

    private static string? Iso(JsonElement value, string path, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        var text = Text(value, 24, path, true);
        Require(text.Length == 24 && CalendarDate(text[..10]) && text[10] == 'T' && text[13] == ':'
            && text[16] == ':' && text[19] == '.' && text[23] == 'Z'
            && Digits(text.AsSpan(11, 2), 23) && Digits(text.AsSpan(14, 2), 59)
            && Digits(text.AsSpan(17, 2), 59) && Digits(text.AsSpan(20, 3), 999),
            path, "应为有效的 UTC 日期时间 yyyy-MM-ddTHH:mm:ss.fffZ");
        return text;
    }

    // Lumen's four-digit ISO years include 0000. Validate its proleptic Gregorian calendar
    // without coercing old dates to .NET DateTimeOffset; exact original dates remain text.
    private static bool CalendarDate(string text)
    {
        if (text.Length != 10 || text[4] != '-' || text[7] != '-'
            || !Digits(text.AsSpan(0, 4), 9999) || !Digits(text.AsSpan(5, 2), 12)
            || !Digits(text.AsSpan(8, 2), 31)) return false;
        var year = int.Parse(text.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(text.AsSpan(5, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(text.AsSpan(8, 2), CultureInfo.InvariantCulture);
        return month >= 1 && day >= 1 && day <= DateTime.DaysInMonth(year == 0 ? 400 : year, month);
    }

    private static bool Digits(ReadOnlySpan<char> text, int max)
    {
        foreach (var character in text) if (character is < '0' or > '9') return false;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= max;
    }

    private static void Require(bool condition, string path, string reason)
    {
        if (!condition) throw Invalid(path, reason);
    }
    private static InvalidDataException Invalid(string path, string reason) => new($"Lumen 数据无效：{path}，{reason}。未导入任何数据。");
    private sealed record LegacyNote(string Id, string Title, string Body, List<string> Tags, bool Pinned, string CreatedAt, string UpdatedAt, string? DeletedAt);
    private sealed record LegacyTask(string Id, string Title, bool Done, string Priority, string DueDate, string CreatedAt, string? CompletedAt);
}
