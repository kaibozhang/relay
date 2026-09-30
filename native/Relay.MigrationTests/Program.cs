using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core;
using Relay.Desktop.Services;

var tests = new List<(string Name, Action<string> Run)>
{
    ("Mappings, trash, dates and honest summary", directory =>
    {
        var note = Note(); note["pinned"] = true; note["body"] = "第一行\r\n第二行\n[[链接]]\n";
        var trash = Note("trash"); trash["deletedAt"] = At;
        var done = TaskItem("done"); done["done"] = true; done["completedAt"] = At;
        var pending = TaskItem("pending"); pending["dueDate"] = "2028-02-29";
        var root = Export([note, trash], [done, pending]);
        root["sessions"] = new JsonArray(new JsonObject { ["minutes"] = 99 });
        root["timer"] = new JsonObject { ["status"] = "running" };
        root["settings"] = new JsonObject { ["dailyGoal"] = 999 };
        var before = DateTimeOffset.UtcNow;
        var preview = Inspect(directory, root);
        Check(preview.NoteCount == 1 && preview.TaskCount == 2 && preview.SkippedTrashCount == 1 && preview.Cards.Count == 3, "Counts");
        var card = preview.Cards[0];
        Check(card.Pinned && card.Status == "parked" && Body(card) == note["body"]!.GetValue<string>(), "Note mapping and exact newlines");
        Check(card.NextAction == "回看这条笔记，写下具体的下一步", "Editable action");
        Check(preview.Cards[1].Status == "done" && preview.Cards[1].CompletedAt == card.CreatedAt, "Done state");
        Check(preview.Cards[2].Status == "parked" && preview.Cards[2].CompletedAt is null, "Pending state");
        Check(preview.Cards[2].Context.Contains("2028-02-29") && preview.Cards[2].SnoozedUntil is null, "Due is text");
        Check(preview.Cards.All(c => c.CreatedAt == card.CreatedAt && c.UpdatedAt == card.CreatedAt && c.CreatedAt >= before && c.CreatedAt <= DateTimeOffset.UtcNow), "Consistent now");
        Check(preview.Cards.All(c => c.ResumeCount == 0 && c.LastResumedAt is null && c.Resources.Count == 0 && c.Checkpoints.Count == 0), "No fake activity");
        foreach (var word in new[] { "sessions", "timer", "settings", "图谱", "纯文字", "跳过回收站", "长标题", "不建立定时提醒" })
            Check(preview.Summary.Contains(word), "Discloses " + word);
        VaultStore.Validate(new VaultDocument { Cards = preview.Cards });
    }),
    ("Paths, URLs and HTML are inert text", directory =>
    {
        var payload = "<script>alert(1)</script>\nfile:///C:/Windows/system.ini\nhttps://invalid.example/steal\n" + @"\\server\share\secret" + "\n" + @"..\..\secret.txt" + "\n[[link]]";
        var note = Note(); note["body"] = payload; note["id"] = @"..\..\do-not-open";
        var task = TaskItem(); task["title"] = @"C:\Windows\System32\cmd.exe /c del *";
        var preview = Inspect(directory, Export([note], [task]));
        Check(Body(preview.Cards[0]) == payload && preview.Cards[1].NextAction == task["title"]!.GetValue<string>(), "Payload unchanged");
        Check(preview.Cards.All(c => c.Resources.Count == 0), "No attachment");
        Check(Directory.GetFiles(directory).Length == 1, "Only fixture exists");
    }),
    ("Long titles retained with surrogate-safe display", directory =>
    {
        var title = new string('题', 199) + "😀" + new string('尾', 99);
        var note = Note(); note["title"] = title;
        var task = TaskItem(); task["title"] = title;
        var preview = Inspect(directory, Export([note], [task]));
        Check(preview.Cards.All(c => c.Title == new string('题', 199) && c.Context.Contains(title)), "Full originals retained");
        Check(preview.Cards[1].NextAction == title && preview.Summary.Contains("2 个长标题"), "Action retains 300 UTF-16 units");
        Check(preview.Cards.All(c => ValidUnicode(c.Title)), "Safe title boundary");
    }),
    ("200k Chinese and surrogate body reconstructs exactly", directory =>
    {
        var body = new string('中', 44999) + "😀\r\n" + string.Concat(Enumerable.Repeat("文😀\n", 38000));
        body += new string('尾', 200000 - body.Length);
        var note = Note(); note["body"] = body;
        var preview = Inspect(directory, Export([note]));
        Check(preview.Cards.Count == 5 && preview.NoteCount == 1, "Continuation count");
        Check(string.Concat(preview.Cards.Select(Body)) == body, "Exact reconstruction");
        Check(preview.Cards.All(c => c.Context.Length <= 50000 && Body(c).Length <= 45000 && ValidUnicode(c.Context) && ValidUnicode(c.Title)), "Length and Unicode limits");
        Check(preview.Cards.Skip(1).All(c => c.Title.Contains("续页")), "Continuation titles");
        Check(preview.Summary.Contains("1 条长笔记") && preview.Summary.Contains("增加 4 张"), "Split disclosed");
    }),
    ("Maximum tags reduce body budget losslessly", directory =>
    {
        var note = Note(); note["title"] = new string('题', 300);
        note["tags"] = new JsonArray(Enumerable.Range(0, 50).Select(i => (JsonNode?)JsonValue.Create(new string('标', 297) + i.ToString("D3"))).ToArray());
        var body = new string('文', 200000); note["body"] = body;
        var preview = Inspect(directory, Export([note]));
        Check(preview.Cards.Count > 5 && preview.Cards.All(c => c.Context.Length <= 50000), "Metadata included in budget");
        Check(string.Concat(preview.Cards.Select(Body)) == body, "No dropped body");
        foreach (var tag in note["tags"]!.AsArray()) Check(preview.Cards[0].Context.Contains(tag!.GetValue<string>()), "No dropped tag");
    }),
    ("Empty notes and whitespace preserved", directory =>
    {
        var note = Note(); note["title"] = ""; note["body"] = ""; note["tags"] = new JsonArray();
        var preview = Inspect(directory, Export([note]));
        Check(preview.Cards.Count == 1 && Body(preview.Cards[0]) == "" && preview.Cards[0].Title == "未命名笔记", "Empty notes");
        var whitespace = Note(); whitespace["body"] = " \r\n\t ";
        Check(Body(Inspect(directory, Export([whitespace])).Cards[0]) == " \r\n\t ", "No trimming");
    }),
    ("Bad version, missing arrays, roots and syntax rejected", directory =>
    {
        foreach (var json in new[] { "[]", "null", "{}", "{\"version\":2,\"notes\":[],\"tasks\":[]}", "{\"version\":\"1\",\"notes\":[],\"tasks\":[]}", "{\"version\":1,\"notes\":[]}", "{\"version\":1,\"notes\":null,\"tasks\":[]}", "{\"version\":1,\"notes\":[],\"tasks\":[],}", "/*comment*/{}", "{", "" }) Reject(directory, json);
    }),
    ("Duplicate decoded fields including ignored trees rejected", directory =>
    {
        Reject(directory, "{\"version\":1,\"version\":1,\"notes\":[],\"tasks\":[]}");
        Reject(directory, "{\"version\":1,\"notes\":[],\"tasks\":[],\"settings\":{\"x\":1,\"\\u0078\":2}}");
        var json = Export([Note()]).ToJsonString();
        Reject(directory, json.Replace("\"pinned\":false", "\"pinned\":false,\"pinned\":true"));
    }),
    ("Strict booleans, strings, tags, priorities and IDs", directory =>
    {
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("id", ""), ("id", 3), ("title", true), ("title", new string('a', 301)),
            ("body", null), ("body", new string('a', 200001)), ("pinned", "false"), ("pinned", 0),
            ("tags", new JsonArray("")), ("tags", new JsonArray(4)), ("tags", new JsonArray(new string('a', 301))),
            ("tags", new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode?)JsonValue.Create("tag")).ToArray())), ("unknown", true)
        }) { var note = Note(); note[field] = value; Reject(directory, Export([note]).ToJsonString()); }
        foreach (var (field, value) in new (string, JsonNode?)[]
        { ("title", " "), ("done", "false"), ("done", 0), ("priority", "urgent"), ("priority", null), ("dueDate", null), ("done", true), ("completedAt", At) })
        { var task = TaskItem(); task[field] = value; Reject(directory, Export(tasks: [task]).ToJsonString()); }
        var missing = Note(); missing.Remove("deletedAt"); Reject(directory, Export([missing]).ToJsonString());
        Reject(directory, Export([Note("same")], [TaskItem("same")]).ToJsonString());
        Reject(directory, Export([Note("same"), Note("same")]).ToJsonString());
    }),
    ("Strict ISO and calendar validation including year zero", directory =>
    {
        foreach (var date in new[] { "2026-02-29T12:34:56.789Z", "2026-06-01T24:00:00.000Z", "2026-06-01T12:34:60.000Z", "2026-06-01", "2026-06-01T12:34:56Z", "2026-06-01T12:34:56.789+00:00", "2026-06-01t12:34:56.789z", "2026-00-00T12:34:56.789Z" })
        {
            foreach (var field in new[] { "createdAt", "updatedAt", "deletedAt" })
            { var note = Note(); note[field] = date; Reject(directory, Export([note]).ToJsonString()); }
            var task = TaskItem(); task["done"] = true; task["completedAt"] = date;
            Reject(directory, Export(tasks: [task]).ToJsonString());
        }
        foreach (var date in new[] { "2026-02-29", "1900-02-29", "2026-13-01", "2026-00-01", "2026-01-00", "2026-04-31", "26-01-01", "2026-1-01" })
        { var task = TaskItem(); task["dueDate"] = date; Reject(directory, Export(tasks: [task]).ToJsonString()); }
        foreach (var date in new[] { "0000-02-29", "2000-02-29", "2028-02-29", "9999-12-31" })
        {
            var note = Note(); note["createdAt"] = date + "T00:00:00.000Z";
            var task = TaskItem(); task["dueDate"] = date;
            Check(Inspect(directory, Export([note], [task])).Cards.Count == 2, "Valid date " + date);
        }
    }),
    ("20 MiB byte boundary checked before parsing", directory =>
    {
        var path = Path.Combine(directory, "boundary.json");
        var valid = Encoding.UTF8.GetBytes("{\"version\":1,\"notes\":[],\"tasks\":[]}");
        var bytes = new byte[LegacyImporter.MaxInputBytes]; bytes.AsSpan().Fill((byte)' '); valid.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        Check(LegacyImporter.Inspect(path).Cards.Count == 0, "Exactly 20 MiB permitted");
        using (var stream = new FileStream(path, FileMode.Append)) stream.WriteByte((byte)' ');
        Check(Throws<InvalidDataException>(() => LegacyImporter.Inspect(path)).Message.Contains("20 MiB"), "One extra byte rejected");
        using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(LegacyImporter.MaxInputBytes + 1L);
        Check(Throws<InvalidDataException>(() => LegacyImporter.Inspect(path)).Message.Contains("20 MiB"), "Oversize before parse");
    }),
    ("BOM accepted, malformed UTF-8 and surrogates rejected", directory =>
    {
        var path = Path.Combine(directory, "encoding.json");
        var json = Export([Note()]).ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(path, json, new UTF8Encoding(true));
        Check(LegacyImporter.Inspect(path).Cards.Count == 1, "BOM accepted");
        File.WriteAllBytes(path, [0xff, 0xfe, 0x7b, 0]); Throws<InvalidDataException>(() => LegacyImporter.Inspect(path));
        File.WriteAllBytes(path, [0xc0, 0xaf]); Throws<InvalidDataException>(() => LegacyImporter.Inspect(path));
        Reject(directory, json.Replace("示例笔记", "\\uD800"));
        Reject(directory, "{\"version\":1,\"notes\":[],\"tasks\":[],\"\\uD800\":0}");
        Reject(directory, "{\"version\":1,\"notes\":[],\"tasks\":[],\"ignored\":\"\\uDC00\"}");
    }),
    ("Depth limit covers ignored root fields", directory =>
    {
        var prefix = "{\"version\":1,\"notes\":[],\"tasks\":[],\"ignored\":";
        Check(InspectText(directory, prefix + new string('[', 31) + "0" + new string(']', 31) + "}").Cards.Count == 0, "Depth 32 accepted");
        Reject(directory, prefix + new string('[', 32) + "0" + new string(']', 32) + "}");
    }),
    ("Fresh GUIDs and detached candidate lists", directory =>
    {
        var root = Export([Note()], [TaskItem()]);
        var first = Inspect(directory, root); var second = Inspect(directory, root);
        Check(first.Cards.Concat(second.Cards).Select(c => c.Id).Distinct().Count() == 4, "Fresh IDs");
        Check(first.Cards.All(c => Guid.TryParse(c.Id, out _)), "Valid GUIDs");
        first.Cards[0].Title = "edited"; first.Cards.Clear();
        Check(second.Cards.Count == 2 && second.Cards[0].Title == "示例笔记", "Independent results");
    }),
    ("5000 card cap includes continuations and existing cards", directory =>
    {
        var tasks = Enumerable.Range(0, 5000).Select(i => TaskItem("t" + i)).ToArray();
        var preview = Inspect(directory, Export(tasks: tasks));
        Check(preview.Cards.Count == 5000, "Exactly 5000 accepted");
        Throws<InvalidDataException>(() => VaultStore.Validate(new VaultDocument { Cards = [new RelayCard { Title = "existing" }, .. preview.Cards] }));
        var ex = Throws<InvalidDataException>(() => Inspect(directory, Export([Note()], tasks.Select(t => (JsonObject)t.DeepClone()).ToArray())));
        Check(ex.Message.Contains("5000"), "Actionable capacity error");
        var longNote = Note(); longNote["body"] = new string('x', 45001);
        Throws<InvalidDataException>(() => Inspect(directory, Export([longNote], Enumerable.Range(0, 4999).Select(i => TaskItem("s" + i)).ToArray())));
    }),
    ("Core-incompatible text is rejected, never sanitized", directory =>
    {
        foreach (var body in new[] { "before\u0000after", "before\u202Eafter" })
        {
            var note = Note(); note["body"] = body;
            var ex = Throws<InvalidDataException>(() => Inspect(directory, Export([note])));
            Check(ex.Message.Contains("不会自动删改正文"), "Actionable lossless rejection");
        }
        var multiline = Note(); multiline["title"] = "title\nsecond line";
        Check(Throws<InvalidDataException>(() => Inspect(directory, Export([multiline]))).Message.Contains("标题含换行"), "Title limitation explained");
    }),
    ("Successful preview also never writes source or vault", directory =>
    {
        var path = Path.Combine(directory, "source.json");
        File.WriteAllText(path, Export([Note()]).ToJsonString());
        var vault = Path.Combine(directory, "vault.json"); File.WriteAllText(vault, "existing");
        var original = File.ReadAllBytes(path);
        var files = Directory.GetFiles(directory).Order().ToArray();
        Check(LegacyImporter.Inspect(path).Cards.Count == 1, "Success");
        Check(original.SequenceEqual(File.ReadAllBytes(path)) && File.ReadAllText(vault) == "existing", "Unchanged data");
        Check(files.SequenceEqual(Directory.GetFiles(directory).Order()), "No writes on success");
    }),
    ("Invalid final row causes no partial output or writes", directory =>
    {
        var vault = Path.Combine(directory, "vault.json"); File.WriteAllText(vault, "existing-user-vault");
        var bad = Note("bad"); bad["deletedAt"] = "not-a-date";
        var path = Path.Combine(directory, "backup.json");
        File.WriteAllText(path, Export([Note("good"), bad]).ToJsonString(), new UTF8Encoding(false));
        var original = File.ReadAllBytes(path); var names = Directory.GetFiles(directory).Order().ToArray();
        MigrationPreview? preview = null;
        Throws<InvalidDataException>(() => preview = LegacyImporter.Inspect(path));
        Check(preview is null, "No returned candidates");
        Check(File.ReadAllText(vault) == "existing-user-vault" && original.SequenceEqual(File.ReadAllBytes(path)), "No modified files");
        Check(names.SequenceEqual(Directory.GetFiles(directory).Order()), "No sidecars");
    })
};

// Test-owned fixtures stay inside the project output, never browser storage or AppData.
var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "migration-fixtures-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixtureRoot);
var failures = 0;
try
{
    foreach (var (name, run) in tests)
    {
        var directory = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(directory); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
    }
}
finally
{
    // Verify the exact absolute deletion target is this run's owned fixture directory.
    var output = Path.GetFullPath(AppContext.BaseDirectory);
    if (Path.GetDirectoryName(fixtureRoot) == Path.TrimEndingDirectorySeparator(output)
        && Path.GetFileName(fixtureRoot).StartsWith("migration-fixtures-", StringComparison.Ordinal))
        Directory.Delete(fixtureRoot, true);
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} migration tests passed.");
return failures == 0 ? 0 : 1;

partial class Program
{
    private const string At = "2026-06-01T12:34:56.789Z";
    private const string Marker = "\n--- Lumen 正文 ---\n";
    private static JsonObject Note(string id = "note") => new()
    {
        ["id"] = id, ["title"] = "示例笔记", ["body"] = "原始正文\n", ["tags"] = new JsonArray("中文", "tag"),
        ["pinned"] = false, ["createdAt"] = At, ["updatedAt"] = At, ["deletedAt"] = null
    };
    private static JsonObject TaskItem(string id = "task") => new()
    {
        ["id"] = id, ["title"] = "做具体的一步", ["done"] = false, ["priority"] = "normal",
        ["dueDate"] = "", ["createdAt"] = At, ["completedAt"] = null
    };
    private static JsonObject Export(JsonObject[]? notes = null, JsonObject[]? tasks = null) => new()
    {
        ["version"] = 1, ["notes"] = new JsonArray((notes ?? []).Select(n => (JsonNode)n).ToArray()),
        ["tasks"] = new JsonArray((tasks ?? []).Select(t => (JsonNode)t).ToArray()), ["updatedAt"] = At
    };
    private static MigrationPreview Inspect(string directory, JsonObject root) => InspectText(directory,
        root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    private static MigrationPreview InspectText(string directory, string json)
    {
        var path = Path.Combine(directory, "input.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return LegacyImporter.Inspect(path);
    }
    private static void Reject(string directory, string json) => Throws<InvalidDataException>(() => InspectText(directory, json));
    private static string Body(RelayCard card)
    {
        var index = card.Context.IndexOf(Marker, StringComparison.Ordinal);
        Check(index >= 0, "Body wrapper exists");
        return card.Context[(index + Marker.Length)..];
    }
    private static bool ValidUnicode(string text)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
