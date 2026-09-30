using System.Text;
using System.Text.Json;
using Relay.Core;

var tests = new (string Name, Action Body)[]
{
    ("CRUD persists, revision and deep clone", Crud),
    ("Atomic backup contains last good revision", Backup),
    ("Corrupt main recovers and preserves bytes", Recovery),
    ("Both corrupt fail closed without overwrite", BothCorrupt),
    ("Missing main recovers existing backup", MissingMain),
    ("Concurrent stale writer is rejected", Concurrency),
    ("Parallel writers cannot lose an update", ParallelWriters),
    ("Failed save leaves document and vault intact", FailedSave),
    ("Strict JSON rejects unknown/case/duplicate/missing/null", StrictJson),
    ("Invalid import never changes vault", InvalidImport),
    ("Export/import is validated and detached", ExportImport),
    ("Count and text limits reject without truncation", Limits),
    ("Only safe resource protocols and local paths", Paths),
    ("IDs status dates and controls validated", Fields),
    ("32 MiB limit applies before parse and after serialization", ByteLimit),
    ("Corrupt backup is preserved before rotation", CorruptBackup),
    ("Recovery rejects changed backup", RecoveryConcurrency),
    ("Failed reload cannot authorize overwrite", FailedReload),
    ("Export cannot overwrite live main or recovery files", ExportProtection),
    ("Malformed Unicode is rejected without replacement", UnicodeValidation),
    ("Sparse 1 GiB input fails fast without copying", OversizedFailsFast)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message = "Assertion failed")
{ if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static void WithVault(Action<VaultStore, string> test)
{
    var root = Path.Combine(AppContext.BaseDirectory, "relay-core-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test(new VaultStore(root), root); }
    finally { Directory.Delete(root, true); }
}
static RelayCard Card(string title = "续页") => new() { Title = title, NextAction = "继续测试", Context = "line 1\nline 2" };
static VaultDocument Seed(VaultStore store)
{ var doc = store.Load(); doc.Cards.Add(Card()); store.Save(doc); return doc; }
static string Json(VaultDocument doc) => JsonSerializer.Serialize(doc);
static void Crud() => WithVault((store, root) =>
{
    var doc = store.Load(); Check(doc.Cards.Count == 0 && doc.Revision == 0 && !File.Exists(store.DataPath));
    doc.Cards.Add(Card()); store.Save(doc); Check(doc.Revision == 1);
    var clone = VaultStore.Clone(doc); clone.Cards[0].Title = "clone"; Check(doc.Cards[0].Title != "clone");
    var loaded = store.Load(); Check(loaded.Cards[0].Title == "续页");
    loaded.Cards[0].Status = "active"; loaded.Cards[0].ResumeCount++; store.Save(loaded);
    Check(new VaultStore(root).Load().Cards[0].ResumeCount == 1);
    loaded.Cards.Clear(); store.Save(loaded); Check(store.Load().Cards.Count == 0 && loaded.Revision == 3);
});
static void Backup() => WithVault((store, _) =>
{
    var doc = Seed(store); var original = File.ReadAllBytes(store.DataPath);
    doc.Cards[0].Title = "new"; store.Save(doc);
    Check(original.SequenceEqual(File.ReadAllBytes(store.DataPath + ".bak")));
    Check(!Directory.GetFiles(store.DirectoryPath, "*.tmp").Any());
});
static void Recovery() => WithVault((store, root) =>
{
    var doc = Seed(store); doc.Cards[0].Title = "second"; store.Save(doc);
    var corrupt = new byte[] { 0xff, 0xfe, 0, 1, 2 }; File.WriteAllBytes(store.DataPath, corrupt);
    var recovery = new VaultStore(root); var recovered = recovery.Load();
    Check(recovered.Cards[0].Title == "续页" && !recovery.IsReadOnly && recovery.RecoveryNotice != null);
    Check(Directory.GetFiles(root, "*.corrupt-*").Any(p => File.ReadAllBytes(p).SequenceEqual(corrupt)));
    Check(File.ReadAllBytes(store.DataPath).SequenceEqual(corrupt), "Load must not replace corrupt main");
    recovery.Save(recovered); Check(new VaultStore(root).Load().Cards[0].Title == "续页");
    Check(recovery.Import(store.DataPath + ".bak").Revision == 1);
});
static void BothCorrupt() => WithVault((store, root) =>
{
    File.WriteAllText(store.DataPath, "bad main"); File.WriteAllText(store.DataPath + ".bak", "bad backup");
    var doc = store.Load(); Check(store.IsReadOnly && doc.Cards.Count == 0 && store.RecoveryNotice != null);
    Throws<InvalidOperationException>(() => store.Save(doc));
    Throws<InvalidOperationException>(() => store.Export(store.DataPath, doc));
    Check(File.ReadAllText(store.DataPath) == "bad main" && File.ReadAllText(store.DataPath + ".bak") == "bad backup");
    Check(Directory.GetFiles(root, "*.corrupt-*").Length >= 2);
});
static void MissingMain() => WithVault((store, root) =>
{
    var doc = Seed(store); store.Save(doc); File.Delete(store.DataPath);
    var recovered = new VaultStore(root); Check(recovered.Load().Cards.Count == 1 && recovered.RecoveryNotice != null);
});
static void Concurrency() => WithVault((store, root) =>
{
    Seed(store); var other = new VaultStore(root); var stale = other.Load(); var doc = store.Load();
    doc.Cards[0].Title = "winner"; store.Save(doc);
    Throws<InvalidOperationException>(() => other.Save(stale)); Check(stale.Revision == 1);
    Check(store.Load().Cards[0].Title == "winner");
    // Even external edits that forget to advance revision must not be overwritten.
    var external = VaultStore.Clone(doc); external.Cards[0].Title = "external";
    File.WriteAllText(store.DataPath, Json(external));
    Throws<InvalidOperationException>(() => store.Save(doc));
});
static void ParallelWriters() => WithVault((store, root) =>
{
    Seed(store); var stores = Enumerable.Range(0, 4).Select(_ => new VaultStore(root)).ToArray();
    var docs = stores.Select(s => s.Load()).ToArray(); var wins = 0;
    Parallel.For(0, stores.Length, i => { try { docs[i].Cards[0].Title = i.ToString(); stores[i].Save(docs[i]); Interlocked.Increment(ref wins); } catch (InvalidOperationException) { } });
    Check(wins == 1 && store.Load().Revision == 2);
});
static void FailedSave() => WithVault((store, _) =>
{
    var doc = Seed(store); var original = File.ReadAllBytes(store.DataPath); var updated = doc.UpdatedAt;
    doc.Cards[0].Title = new string('x', 201); Throws<InvalidDataException>(() => store.Save(doc));
    Check(doc.Revision == 1 && doc.UpdatedAt == updated && original.SequenceEqual(File.ReadAllBytes(store.DataPath)));
    doc.Cards[0].Title = "valid";
    using (var locked = new FileStream(store.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Throws<IOException>(() => store.Save(doc));
    Check(doc.Revision == 1 && original.SequenceEqual(File.ReadAllBytes(store.DataPath)));
});
static void StrictJson() => WithVault((store, root) =>
{
    var good = Json(new VaultDocument()); Check(good.Contains("\"cards\"") && !good.Contains("\"Cards\""));
    var path = Path.Combine(root, "import.json");
    foreach (var bad in new[] { "{}", "null", good.Replace("\"cards\"", "\"Cards\""), good.Insert(1, "\"unknown\":1,"), good.Insert(1, "\"version\":1,"), good.Replace("\"cards\":[]", "\"cards\":null"), good.Replace("\"version\":1", "\"version\":2") })
    { File.WriteAllText(path, bad); Throws<InvalidDataException>(() => store.Import(path)); }
});
static void InvalidImport() => WithVault((store, root) =>
{
    Seed(store); var original = File.ReadAllBytes(store.DataPath); var path = Path.Combine(root, "bad.json");
    File.WriteAllText(path, "{broken"); Throws<InvalidDataException>(() => store.Import(path));
    Check(original.SequenceEqual(File.ReadAllBytes(store.DataPath)));
});
static void ExportImport() => WithVault((store, root) =>
{
    var doc = Seed(store); var path = Path.Combine(root, "export.json"); store.Export(path, doc);
    var imported = store.Import(path); imported.Cards[0].Title = "imported";
    Check(store.Load().Cards[0].Title == "续页"); store.Save(imported); Check(store.Load().Cards[0].Title == "imported");
    var before = File.ReadAllBytes(path); imported.Cards[0].Title = new string('x', 201);
    Throws<InvalidDataException>(() => store.Export(path, imported)); Check(before.SequenceEqual(File.ReadAllBytes(path)));
    Throws<InvalidOperationException>(() => store.Export(store.DataPath + ".bak", doc));
});
static void Limits()
{
    var doc = new VaultDocument { Cards = [Card()] }; var card = doc.Cards[0];
    card.Title = new string('t', 200); card.NextAction = new string('n', 2000); card.Context = new string('c', 50000); VaultStore.Validate(doc);
    card.Title += "x"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.Title = "ok";
    card.NextAction += "x"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.NextAction = "ok";
    card.Context += "x"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.Context = "ok";
    card.Resources = Enumerable.Range(0, 100).Select(_ => new ResourceRef { Target = @"C:\work\file.txt" }).ToList(); VaultStore.Validate(doc);
    card.Resources.Add(new ResourceRef { Target = @"C:\work\file.txt" }); Throws<InvalidDataException>(() => VaultStore.Validate(doc));
    doc.Cards = Enumerable.Range(0, 5001).Select(_ => Card()).ToList(); Throws<InvalidDataException>(() => VaultStore.Validate(doc));
}
static void Paths()
{
    var resource = new ResourceRef(); var doc = new VaultDocument { Cards = [new RelayCard { Resources = [resource] }] };
    foreach (var path in new[] { @"C:\file.txt", "D:/folder/thing.txt", @"C:\" }) { resource.Target = path; VaultStore.Validate(doc); }
    foreach (var path in new[] { "relative.txt", @"\rooted", @"C:relative", @"\\server\share", "//server/share", @"\\?\C:\file", @"\\.\device", @"C:\file:stream", @"C:\bad?.txt", @"C:\CON" })
    { resource.Target = path; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); }
    resource.Kind = "url";
    foreach (var url in new[] { "https://example.com/a?q=1", "http://localhost:8080/" }) { resource.Target = url; VaultStore.Validate(doc); }
    foreach (var url in new[] { "javascript:alert(1)", "file:///C:/file", "ftp://example.com", "https://", "https://example.com/\nattack" })
    { resource.Target = url; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); }
}
static void Fields()
{
    var doc = new VaultDocument { Cards = [Card()] }; var card = doc.Cards[0];
    card.Context = "safe\n\r\ttext"; VaultStore.Validate(doc);
    card.Context = "bad\0"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.Context = "ok";
    card.Title = "bad\u202Etitle"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.Title = "ok";
    card.Status = "unknown"; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.Status = "parked";
    card.CreatedAt = default; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.CreatedAt = card.UpdatedAt;
    card.ResumeCount = -1; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); card.ResumeCount = 0;
    doc.Cards.Add(VaultStore.Clone(doc).Cards[0]); Throws<InvalidDataException>(() => VaultStore.Validate(doc));
}
static void CorruptBackup() => WithVault((store, root) =>
{
    var doc = Seed(store); File.WriteAllText(store.DataPath + ".bak", "broken backup");
    store.Save(doc);
    Check(store.Import(store.DataPath + ".bak").Revision == 1);
    Check(Directory.GetFiles(root, "*.corrupt-*").Any(p => File.ReadAllText(p) == "broken backup"));
});
static void RecoveryConcurrency() => WithVault((store, root) =>
{
    var doc = Seed(store); store.Save(doc); File.WriteAllText(store.DataPath, "broken main");
    var recovered = new VaultStore(root); var backup = recovered.Load();
    File.WriteAllText(store.DataPath + ".bak", Json(new VaultDocument()));
    Throws<InvalidOperationException>(() => recovered.Save(backup));
    Check(File.ReadAllText(store.DataPath) == "broken main" && backup.Revision == 1);
});
static void FailedReload() => WithVault((store, _) =>
{
    var doc = Seed(store);
    using (var locked = new FileStream(store.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    { Throws<IOException>(() => store.Load()); Check(store.IsReadOnly); Throws<IOException>(() => store.Save(doc)); }
    Check(store.Load().Revision == 1);
});
static void ExportProtection() => WithVault((store, _) =>
{
    var doc = Seed(store); store.Save(doc); var main = File.ReadAllBytes(store.DataPath); var backup = File.ReadAllBytes(store.DataPath + ".bak");
    foreach (var path in new[] { store.DataPath, store.DataPath + ".bak", store.DataPath + ".corrupt-evidence", Path.Combine(store.DirectoryPath, ".", "vault.json"), store.DataPath.ToUpperInvariant() })
        Throws<InvalidOperationException>(() => store.Export(path, doc));
    Check(main.SequenceEqual(File.ReadAllBytes(store.DataPath)) && backup.SequenceEqual(File.ReadAllBytes(store.DataPath + ".bak")));
});
static void UnicodeValidation() => WithVault((store, root) =>
{
    var doc = new VaultDocument { Cards = [Card()] };
    foreach (var text in new[] { "\uD800", "\uDC00", "a\uD800b" })
    { doc.Cards[0].Context = text; Throws<InvalidDataException>(() => VaultStore.Validate(doc)); }
    doc.Cards[0].Context = "中文 😀"; VaultStore.Validate(doc);
    var path = Path.Combine(root, "unicode.json");
    // Replace a known ASCII field to exercise malformed escaped JSON surrogates.
    doc.Cards[0].Title = "sentinel";
    File.WriteAllText(path, Json(doc).Replace("sentinel", "\\uD800"));
    Throws<InvalidDataException>(() => store.Import(path));
    File.WriteAllBytes(path, [0xff, 0xfe, 0x7b, 0x7d]); Throws<InvalidDataException>(() => store.Import(path));
});
static void OversizedFailsFast() => WithVault((store, root) =>
{
    // SetLength only; no giant byte array or content writes.
    using (var file = File.Create(store.DataPath)) file.SetLength(1024L * 1024 * 1024);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Throws<InvalidDataException>(() => store.Import(store.DataPath));
    Throws<InvalidDataException>(() => store.Load());
    timer.Stop();
    Check(timer.Elapsed < TimeSpan.FromSeconds(1), $"Oversized rejection took {timer.Elapsed}");
    Check(store.IsReadOnly && store.RecoveryNotice?.Contains("32 MiB") == true);
    Throws<InvalidDataException>(() => store.Save(new VaultDocument()));
    Check(new FileInfo(store.DataPath).Length == 1024L * 1024 * 1024);
    Check(Directory.GetFiles(root).Length == 1, "Oversized file must not create copies or temporary files");
    Console.WriteLine($"  1 GiB metadata-only rejection: {timer.Elapsed.TotalMilliseconds:F1} ms");
});
static void ByteLimit() => WithVault((store, root) =>
{
    var path = Path.Combine(root, "oversize.json");
    using (var file = File.Create(path)) file.SetLength(32L * 1024 * 1024 + 1);
    Throws<InvalidDataException>(() => store.Import(path));
    var doc = new VaultDocument { Cards = Enumerable.Range(0, 700).Select(_ => new RelayCard { Context = new string('x', 50000) }).ToList() };
    Throws<InvalidDataException>(() => VaultStore.Validate(doc));
});
