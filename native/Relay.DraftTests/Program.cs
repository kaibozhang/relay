using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core;
using Relay.Desktop.Services;

// Only unique fixtures under this project's build directory; never uses a user's vault/profile/temp.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "draft-fixtures-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var passed = 0;
var skipped = 0;
try
{
    Run("absent list/delete do not create directories", () =>
    {
        var store = Store("absent");
        Check(store.List().Count == 0 && store.Warning is null, "empty absent list");
        store.Delete(Guid.NewGuid().ToString("N"));
        Check(!Directory.Exists(Path.GetDirectoryName(store.DirectoryPath)), "read/delete created vault");
    });
    Run("save/list/replace/delete blank fields and detached Unicode resources", () =>
    {
        var store = Store("roundtrip");
        var card = new RelayCard { Title = "", NextAction = "", Context = "想法 😀 café\n下一行", Pinned = true,
            Resources = [new ResourceRef { Label = "本地文件", Target = @"Z:\does-not-exist\data.txt", Kind = "file" }] };
        var before = JsonSerializer.Serialize(card);
        store.Save(card);
        Check(before == JsonSerializer.Serialize(card), "save mutated input");
        var saved = store.List().Single();
        Check(saved.Version == 1 && saved.Card.Context == card.Context && saved.Card.NextAction == "" && saved.Card.Title == "", "roundtrip mismatch");
        Check(saved.Card.Resources[0].Target == card.Resources[0].Target, "resource altered");
        saved.Card.Resources.Clear(); card.Context = "changed";
        Check(store.List().Single().Card.Resources.Count == 1, "returned graph aliased persistence");
        store.Save(card);
        Check(store.List().Single().Card.Context == "changed", "replacement missing");
        Check(Directory.GetFiles(store.DirectoryPath).Length == 1, "backup/temp created");
        store.Delete(card.Id); store.Delete(card.Id);
        Check(store.List().Count == 0 && Directory.Exists(store.DirectoryPath), "delete was not exact/idempotent");
    });
    Run("strict schema and malformed files preserved", () =>
    {
        var store = Store("schema");
        var card = new RelayCard(); store.Save(card);
        var path = DraftPath(store, card.Id);
        var original = File.ReadAllText(path);
        var mutations = new List<string>
        {
            "{", "null", "[]", "{}", original.Replace("\"version\": 1", "\"version\": 2"),
            original.Replace("\"version\": 1,", ""),
            original.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"),
            original.Replace("\"version\": 1", "\"version\": 1, \"unexpected\": true"),
            original.Replace("\"version\"", "\"Version\""),
            original.Replace("\"title\": \"\"", "\"title\": \"\", \"title\": \"duplicate\""),
            original.Replace("\"title\": \"\"", "\"title\": \"\", \"unexpected\": true")
        };
        foreach (var mutate in new Action<JsonObject>[]
        {
            j => j.Remove("card"), j => j.Remove("savedAt"), j => j["card"] = null,
            j => j["card"]!["id"] = Guid.NewGuid().ToString("N"),
            j => j["card"]!.AsObject().Remove("nextAction"),
            j => j["savedAt"] = "0001-01-01T00:00:00Z",
            j => j["card"]!["context"] = null
        })
        {
            var node = JsonNode.Parse(original)!.AsObject(); mutate(node); mutations.Add(node.ToJsonString());
        }
        foreach (var text in mutations)
        {
            Check(text != original, "schema test did not mutate");
            File.WriteAllText(path, text);
            Check(store.List().Count == 0 && store.Warning is not null, "bad schema accepted: " + text);
            Check(File.ReadAllText(path) == text, "corrupt evidence changed");
            Throws(() => store.Save(card));
            Check(File.ReadAllText(path) == text, "save overwrote corrupt evidence");
        }
        File.WriteAllBytes(path, [0xff, 0xfe, 0x80]);
        Check(store.List().Count == 0 && store.Warning is not null, "invalid UTF8 accepted");
        Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 0xff, 0xfe, 0x80 }), "invalid UTF8 changed");
        store.Delete(card.Id);
        Check(store.Warning is null, "warning not reset");
        store.Save(card);
        Check(store.List().Count == 1 && store.Warning is null, "valid draft not restored");
    });
    Run("strict GUID IDs block traversal without touching outside data", () =>
    {
        var store = Store("ids");
        var sentinel = Path.Combine(root, "outside.json"); File.WriteAllText(sentinel, "sentinel");
        foreach (var id in new[] { "../outside", @"..\outside", sentinel, "", " ", Guid.NewGuid().ToString("D"),
            new string('a', 31), new string('g', 32), new string('a', 32) + ":stream", new string('a', 32) + ".json", null! })
        {
            Throws(() => store.Save(new RelayCard { Id = id }));
            Check(store.Warning is not null, "save warning absent");
            Throws(() => store.Delete(id));
            Check(store.Warning is not null, "delete warning absent");
        }
        Check(File.ReadAllText(sentinel) == "sentinel" && !Directory.Exists(store.DirectoryPath), "traversal touched filesystem");
        var uppercase = new RelayCard { Id = Guid.NewGuid().ToString("N").ToUpperInvariant() };
        store.Save(uppercase); Check(store.List().Single().Card.Id == uppercase.Id, "GUID N uppercase rejected");
    });
    Run("new capture only and core Unicode validation", () =>
    {
        var store = Store("validation");
        foreach (var mutate in new Action<RelayCard>[]
        {
            c => c.Status = "active", c => c.Status = "done", c => c.ResumeCount = 1,
            c => c.LastResumedAt = DateTimeOffset.UtcNow, c => c.CompletedAt = DateTimeOffset.UtcNow,
            c => c.SnoozedUntil = DateTimeOffset.UtcNow, c => c.Checkpoints.Add(new Checkpoint()),
            c => c.Title = "\uD800", c => c.Title = "\u202E", c => c.Title = "line\nbreak",
            c => c.NextAction = "\u0000", c => c.Resources = null!, c => c.Title = new string('x', 201)
        })
        {
            var card = new RelayCard(); mutate(card); Throws(() => store.Save(card));
            Check(store.Warning is not null, "validation warning absent");
        }
        Check(!Directory.Exists(store.DirectoryPath), "failed validation created directory");
    });
    Run("50 file capacity rejects new ID but permits replacement", () =>
    {
        var store = Store("capacity");
        var cards = Enumerable.Range(0, 50).Select(_ => new RelayCard()).ToArray();
        foreach (var card in cards) store.Save(card);
        var before = Directory.GetFiles(store.DirectoryPath).ToDictionary(p => p, File.ReadAllText);
        Throws(() => store.Save(new RelayCard()));
        Check(store.Warning is not null && Directory.GetFiles(store.DirectoryPath).Length == 50, "capacity not enforced");
        Check(before.All(kv => File.ReadAllText(kv.Key) == kv.Value), "capacity deleted/modified old file");
        cards[0].Context = "replacement at limit"; store.Save(cards[0]);
        Check(store.List().Count == 50 && store.Warning is null, "replacement at capacity failed");
        store.Delete(cards[1].Id); store.Save(new RelayCard());
        Check(store.List().Count == 50, "deleted slot not reusable");
    });
    Run("extra files bounded and newest readable results sorted", () =>
    {
        var store = Store("extras"); Directory.CreateDirectory(store.DirectoryPath);
        for (var i = 0; i < 75; i++)
        {
            var card = new RelayCard();
            WriteDraft(store, new CaptureDraft { Card = card, SavedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z").AddMinutes(i) });
        }
        var list = store.List();
        Check(list.Count == 50 && store.Warning is not null, "extras missing warning/limit");
        Check(list.Zip(list.Skip(1)).All(p => p.First.SavedAt >= p.Second.SavedAt), "not sorted descending");
        Check(list[0].SavedAt.Minute == 14 && list[0].SavedAt.Hour == 1, "newest absent");
        Check(Directory.GetFiles(store.DirectoryPath).Length == 75, "extras removed");
        for (var i = 0; i < 250; i++) File.WriteAllText(Path.Combine(store.DirectoryPath, $"junk-{i}.txt"), "untouched");
        Check(store.List().Count <= 50 && store.Warning!.Contains("200 项"), "scan not bounded");
        Throws(() => store.Save(new RelayCard()));
        Check(Directory.GetFiles(store.DirectoryPath).Length == 325, "bounded read/save removed files");
        var corrupt = Store("corrupt-capacity"); Directory.CreateDirectory(corrupt.DirectoryPath);
        for (var i = 0; i < 50; i++) File.WriteAllText(Path.Combine(corrupt.DirectoryPath, $"bad-{i}.json"), "{");
        Throws(() => corrupt.Save(new RelayCard()));
        Check(corrupt.List().Count == 0 && corrupt.Warning is not null, "invalid files not accounted for");
    });
    Run("1 MiB read/write size limit and failed save preserves old draft", () =>
    {
        var store = Store("size"); var card = new RelayCard(); store.Save(card);
        var path = DraftPath(store, card.Id); var old = File.ReadAllBytes(path);
        for (var i = 0; i < 40; i++) card.Resources.Add(new ResourceRef { Label = "resource", Target = @"C:\" + new string('x', 30000) });
        Throws(() => store.Save(card));
        Check(store.Warning is not null && old.SequenceEqual(File.ReadAllBytes(path)), "oversized save damaged old file");
        Check(Directory.GetFiles(store.DirectoryPath).Length == 1, "oversized save left temp");
        using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(1024 * 1024 + 1);
        Check(store.List().Count == 0 && store.Warning is not null, "oversized file accepted");
        Check(new FileInfo(path).Length == 1024 * 1024 + 1, "oversized file changed");
    });
    Run("atomic replacement failure preserves old file and cleans its temp", () =>
    {
        if (!OperatingSystem.IsWindows()) { Skip("Windows sharing-lock failure fixture"); return; }
        var store = Store("failed-replace"); var card = new RelayCard(); store.Save(card);
        var path = DraftPath(store, card.Id); var original = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            card.Context = "replacement must fail"; Throws(() => store.Save(card));
            Check(store.Warning is not null, "failed replacement warning absent");
        }
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "failed replacement modified old file");
        Check(Directory.GetFiles(store.DirectoryPath).Length == 1, "failed replacement leaked temp/backup");
        store.Save(card); store.Delete(card.Id); store.Delete(card.Id);
        Check(store.List().Count == 0, "deleted draft resurrected");
    });
    Run("symlink directory/file and ancestor rejected", () =>
    {
        var outside = Path.Combine(root, "link-target"); Directory.CreateDirectory(outside);
        var store = Store("links"); Directory.CreateDirectory(Path.GetDirectoryName(store.DirectoryPath)!);
        try { Directory.CreateSymbolicLink(store.DirectoryPath, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Skip("symlink creation unavailable: " + ex.GetType().Name + " / " + ex.Message); return; }
        try
        {
            Check(store.List().Count == 0 && store.Warning is not null, "linked directory accepted");
            Throws(() => store.Save(new RelayCard())); Throws(() => store.Delete(Guid.NewGuid().ToString("N")));
            Check(Directory.GetFiles(outside).Length == 0, "directory link followed for write");
        }
        finally { Directory.Delete(store.DirectoryPath); }
        Directory.CreateDirectory(store.DirectoryPath);
        var card = new RelayCard(); var target = Path.Combine(outside, "sentinel.json"); File.WriteAllText(target, "sentinel");
        var link = DraftPath(store, card.Id); File.CreateSymbolicLink(link, target);
        try
        {
            Check(store.List().Count == 0 && store.Warning is not null, "linked file accepted");
            Throws(() => store.Save(card)); Throws(() => store.Delete(card.Id));
            Check(File.ReadAllText(target) == "sentinel", "file link target changed");
        }
        finally { File.Delete(link); }
        var ancestor = Path.Combine(root, "linked-vault"); Directory.CreateSymbolicLink(ancestor, outside);
        try
        {
            var ancestorStore = new CaptureDraftStore(ancestor);
            Check(ancestorStore.List().Count == 0 && ancestorStore.Warning is not null, "ancestor link accepted");
            Throws(() => ancestorStore.Save(card));
        }
        finally { Directory.Delete(ancestor); }
        var dangling = DraftPath(store, card.Id); File.CreateSymbolicLink(dangling, Path.Combine(outside, "missing.json"));
        try { Throws(() => store.Save(card)); Throws(() => store.Delete(card.Id)); }
        finally { File.Delete(dangling); }
    });
    Run("delete leaves unrelated files directories and stale backups alone", () =>
    {
        var store = Store("cleanup"); var card = new RelayCard(); store.Save(card);
        File.WriteAllText(DraftPath(store, card.Id) + ".bak", "not a draft");
        File.WriteAllText(Path.Combine(store.DirectoryPath, ".foreign.tmp"), "foreign");
        var nested = Path.Combine(store.DirectoryPath, "nested"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "user.txt"), "user");
        store.Delete(card.Id); store.Delete(card.Id);
        Check(store.List().Count == 0, "backup resurrected");
        Check(File.ReadAllText(Path.Combine(nested, "user.txt")) == "user", "user directory cleanup attempted");
        Check(Directory.GetFiles(store.DirectoryPath).Length == 2, "unrelated files removed");
    });
    Console.WriteLine($"PASS: {passed} test groups; explicit skips: {skipped}.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex); return 1;
}
finally
{
    // Verify the exact generated fixture root before any recursive cleanup.
    var expectedParent = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    if (Path.GetDirectoryName(root) != expectedParent || !Path.GetFileName(root).StartsWith("draft-fixtures-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside generated fixture root.");
    Directory.Delete(root, recursive: true);
}

CaptureDraftStore Store(string name) => new(Path.Combine(root, name));
static string DraftPath(CaptureDraftStore store, string id) => Path.Combine(store.DirectoryPath, id + ".json");
static void WriteDraft(CaptureDraftStore store, CaptureDraft draft) => File.WriteAllText(DraftPath(store, draft.Card.Id), JsonSerializer.Serialize(draft));
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException) { return; }
    throw new Exception("Expected operation to reject unsafe input or fail safely.");
}
void Run(string name, Action test)
{
    var skipsBefore = skipped;
    test();
    if (skipped == skipsBefore) { passed++; Console.WriteLine("PASS " + name); }
}
void Skip(string reason) { skipped++; Console.WriteLine("SKIP " + reason); }
