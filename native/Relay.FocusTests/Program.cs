using System.Text;
using System.Text.Json.Nodes;
using Relay.Desktop.Services;

var epoch = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2));
var tests = new List<(string Name, Action<string> Run)>
{
    ("Empty store and no-op operations", directory =>
    {
        var store = Open(directory);
        Check(store.Current is null && store.Warning is null, "Fresh store should be empty.");
        Check(store.Remaining(epoch) == TimeSpan.Zero && !store.IsDue(epoch), "Empty store cannot expire.");
        store.Pause(epoch); store.Resume(epoch); store.MarkCompletionPending(epoch); store.AcknowledgeCompletion();
    }),
    ("Countdown, exact expiry and restart", directory =>
    {
        var store = Open(directory);
        store.Start("card-one", 25, epoch);
        string id = store.Current!.Id;
        Check(store.Remaining(epoch.AddMinutes(5)) == TimeSpan.FromMinutes(20), "Countdown is deadline based.");
        Check(store.Remaining(epoch.AddHours(-1)) == TimeSpan.FromMinutes(25), "Clock rollback must be bounded.");
        store = Open(directory);
        Check(store.Current!.Id == id && store.Current.StartedAt == epoch, "Identity and DateTimeOffset survive restart.");
        Check(!store.IsDue(epoch.AddMinutes(25).AddTicks(-1)), "Not due before exact deadline.");
        Check(store.IsDue(epoch.AddMinutes(25)), "Due at exact deadline.");
        Check(store.Remaining(epoch.AddDays(20)) == TimeSpan.Zero, "Expired remainder clamps to zero.");
    }),
    ("Pause, restart, resume and repeated pause", directory =>
    {
        var store = Open(directory);
        store.Start("card", 2, epoch);
        store.Pause(epoch.AddSeconds(30.5));
        Check(store.Current!.Paused && store.Current.Deadline is null, "Pause removes deadline.");
        store = Open(directory);
        Check(store.Remaining(epoch.AddDays(7)) == TimeSpan.FromSeconds(89.5), "Paused remainder is durable.");
        Check(!store.IsDue(epoch.AddDays(7)), "Paused store is not due.");
        store.Pause(epoch.AddDays(7));
        store.Resume(epoch.AddDays(8));
        store.Resume(epoch.AddDays(9));
        Check(store.Remaining(epoch.AddDays(8).AddSeconds(20)) == TimeSpan.FromSeconds(69.5), "Repeated resume is a no-op.");
        store.Pause(epoch.AddDays(8).AddSeconds(20));
        store.Resume(epoch.AddDays(10));
        Check(Open(directory).IsDue(epoch.AddDays(10).AddSeconds(69.5)), "Resumed expiry survives restart.");
    }),
    ("Completion pending, restart, deduplication and acknowledgement", directory =>
    {
        var store = Open(directory);
        store.Start("card", 1, epoch);
        Throws<InvalidOperationException>(() => store.MarkCompletionPending(epoch));
        Throws<InvalidOperationException>(() => store.AcknowledgeCompletion());
        store.MarkCompletionPending(epoch.AddMinutes(1));
        string id = store.Current!.Id;
        var checkpoints = new HashSet<string>();
        checkpoints.Add("Focus completed [focus:" + id + "]");
        store = Open(directory); // Crash after checkpoint save, before acknowledgement.
        Check(store.Current!.CompletionPending && store.IsDue(epoch.AddYears(-1)), "Pending intent survives clock changes/restart.");
        store.MarkCompletionPending(epoch);
        store.Pause(epoch); store.Resume(epoch);
        Throws<InvalidOperationException>(() => store.Start("replacement", 5, epoch));
        checkpoints.Add("Focus completed [focus:" + store.Current!.Id + "]");
        Check(checkpoints.Count == 1, "Parent marker deduplicates checkpoint.");
        store.AcknowledgeCompletion(); store.AcknowledgeCompletion();
        Check(Open(directory).Current is null, "Acknowledgement clears durable state.");
        Check(File.ReadAllText(Live(directory)).Trim() == "null", "Live acknowledgement is a tombstone.");
        Check(File.ReadAllText(Backup(directory)).Trim() == "null", "Backup acknowledgement is a tombstone.");
        File.Delete(Live(directory));
        Check(Open(directory).Current is null, "Backup cannot resurrect acknowledged completion.");
    }),
    ("Pause at expiry becomes pending", directory =>
    {
        var store = Open(directory);
        store.Start("card", 1, epoch);
        store.Pause(epoch.AddMinutes(2));
        Check(store.Current!.CompletionPending && !store.Current.Paused, "An expired session cannot be paused indefinitely.");
    }),
    ("Cancel persists tombstones and prevents recovery resurrection", directory =>
    {
        var store = Open(directory);
        store.Start("card", 10, epoch); store.Pause(epoch.AddSeconds(10)); store.Cancel();
        Check(store.Current is null && Open(directory).Current is null, "Cancel survives restart.");
        File.WriteAllText(Live(directory), "broken after cancel");
        store = Open(directory);
        Check(store.Current is null && store.Warning is not null, "Corrupt live recovers null backup.");
        store.Cancel();
        File.Delete(Live(directory));
        Check(Open(directory).Current is null, "Missing live cannot recover cancelled session.");
    }),
    ("Invalid inputs never mutate state", directory =>
    {
        var store = Open(directory);
        store.Start("valid", 180, epoch);
        var before = store.Current;
        foreach (int minutes in new[] { 0, -1, 181, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => store.Start("card", minutes, epoch));
        foreach (string id in new[] { "", " ", "bad\ncard", new string('x', 129), new string('x', 50000) })
            Throws<ArgumentException>(() => store.Start(id, 1, epoch));
        Throws<ArgumentOutOfRangeException>(() => store.Start("card", 1, DateTimeOffset.MinValue));
        Throws<ArgumentOutOfRangeException>(() => store.Start("card", 1, DateTimeOffset.MaxValue));
        Check(ReferenceEquals(before, store.Current), "Rejected inputs do not mutate state.");
        store.Start(new string('x', 128), 1, epoch);
        Check(Open(directory).Current!.CardId.Length == 128, "Maximum id length is accepted.");
    }),
    ("Failed persistence leaves all transitions unchanged", directory =>
    {
        var store = Open(directory);
        store.Start("card", 1, epoch);
        void Fail(Action operation)
        {
            var before = store.Current;
            byte[] bytes = File.ReadAllBytes(Live(directory));
            using (var locked = new FileStream(Backup(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ThrowsDisk(operation);
            Check(ReferenceEquals(before, store.Current), "Failed save must retain the exact old snapshot.");
            Check(bytes.SequenceEqual(File.ReadAllBytes(Live(directory))), "Failed save leaves live bytes unchanged.");
            Check(store.Warning is not null, "Save failure must be visible.");
        }
        Fail(() => store.Start("new-card", 2, epoch));
        Fail(() => store.Pause(epoch.AddSeconds(10)));
        Fail(() => store.Cancel());
        Fail(() => store.MarkCompletionPending(epoch.AddMinutes(1)));
        store.Pause(epoch.AddSeconds(10));
        Fail(() => store.Resume(epoch.AddMinutes(2)));
        store.Resume(epoch.AddMinutes(2));
        store.MarkCompletionPending(epoch.AddMinutes(3));
        Fail(() => store.AcknowledgeCompletion());
        Check(Open(directory).Current!.CompletionPending, "Failed acknowledgement preserves pending restart state.");
    }),
    ("Live rename failure after backup commit keeps old state", directory =>
    {
        var store = Open(directory);
        store.Start("card", 1, epoch);
        var before = store.Current;
        byte[] bytes = File.ReadAllBytes(Live(directory));
        // Readers are allowed, but delete-sharing is not: preflight reads pass; rename fails.
        using (var locked = new FileStream(Live(directory), FileMode.Open, FileAccess.Read, FileShare.Read))
            ThrowsDisk(() => store.Cancel());
        Check(ReferenceEquals(before, store.Current), "Failed live commit cannot publish cancellation.");
        Check(bytes.SequenceEqual(File.ReadAllBytes(Live(directory))), "Old live file survives failed rename.");
        Check(Open(directory).Current!.Id == before!.Id, "Live remains authoritative over backup tombstone.");
        Check(Directory.GetFiles(directory, "*.tmp-*").Length == 0, "Failed writes clean their own staging files.");
    }),
    ("Unwritable initial directory leaves empty state", directory =>
    {
        string blocker = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blocker, "sentinel");
        var store = new FocusSessionStore(blocker);
        ThrowsDisk(() => store.Start("card", 1, epoch));
        Check(store.Current is null, "Initial failed start is not published.");
        Check(File.ReadAllText(blocker) == "sentinel", "External blocker is untouched.");
    }),
    ("Malformed live recovery preserves exact bytes", directory =>
    {
        var store = Open(directory);
        store.Start("card", 10, epoch);
        string id = store.Current!.Id;
        store.Pause(epoch.AddMinutes(1)); // Backup holds a valid running session.
        byte[] malformed = [0xff, 0xfe, 0x00, 0x7b, 0x80];
        File.WriteAllBytes(Live(directory), malformed);
        store = Open(directory);
        Check(store.Current!.Id == id && !store.Current.Paused, "Recover previous valid backup.");
        Check(store.Warning is not null, "Recovery surfaces warning.");
        Check(File.ReadAllBytes(Live(directory)).SequenceEqual(malformed), "Load does not silently overwrite corruption.");
        Check(Directory.GetFiles(directory, "focus.json.corrupt-*").Any(path => File.ReadAllBytes(path).SequenceEqual(malformed)), "Exact corrupt bytes are preserved.");
        store.Start("replacement", 5, epoch);
        Check(Open(directory).Current!.CardId == "replacement", "Explicit mutation can persist after preservation.");
        Check(store.Warning is not null, "Successful mutation does not hide corruption warning.");
    }),
    ("Both corrupt files preserve evidence before explicit reset", directory =>
    {
        File.WriteAllText(Live(directory), "live-broken");
        File.WriteAllText(Backup(directory), "backup-broken");
        var store = Open(directory);
        Check(store.Current is null && store.Warning is not null, "No valid state recovers safely to empty with warning.");
        Check(Directory.GetFiles(directory, "*.corrupt-*").Length == 2, "Both files have evidence.");
        store.Cancel();
        Check(File.ReadAllText(Live(directory)).Trim() == "null", "Explicit reset persists null.");
    }),
    ("Missing live recovers backup; valid live wins over corrupt backup", directory =>
    {
        var store = Open(directory);
        store.Start("card", 10, epoch); store.Pause(epoch.AddMinutes(1));
        string id = store.Current!.Id;
        File.Delete(Live(directory));
        store = Open(directory);
        Check(store.Current!.Id == id && store.Warning is not null, "Missing live recovers valid backup.");
        store.Pause(epoch.AddMinutes(2));
        File.WriteAllText(Backup(directory), "bad backup");
        store = Open(directory);
        Check(store.Current!.Paused && store.Warning is not null, "Valid live wins while invalid backup is reported.");
    }),
    ("Read failure retains current state and does not erase bytes", directory =>
    {
        var store = Open(directory);
        store.Start("card", 1, epoch);
        var before = store.Current;
        using (var locked = new FileStream(Live(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ThrowsDisk(store.Load);
        Check(ReferenceEquals(before, store.Current), "Failed load is transactional.");
        Check(Open(directory).Current!.Id == before!.Id, "Read failure cannot damage valid disk state.");
    })
};

var invalidJson = new Dictionary<string, Func<JsonObject, string>>
{
    ["unknown field"] = obj => { obj["external"] = "unexpected"; return obj.ToJsonString(); },
    ["duplicate field"] = obj => obj.ToJsonString().Replace("{", "{\"minutes\":1,", StringComparison.Ordinal),
    ["missing field"] = obj => { obj.Remove("completionPending"); return obj.ToJsonString(); },
    ["wrong case"] = obj => { obj["Minutes"] = obj["minutes"]!.DeepClone(); obj.Remove("minutes"); return obj.ToJsonString(); },
    ["fractional minutes"] = obj => { obj["minutes"] = 1.5; return obj.ToJsonString(); },
    ["zero minutes"] = obj => { obj["minutes"] = 0; return obj.ToJsonString(); },
    ["excessive minutes"] = obj => { obj["minutes"] = 181; return obj.ToJsonString(); },
    ["string minutes"] = obj => { obj["minutes"] = "10"; return obj.ToJsonString(); },
    ["long card id"] = obj => { obj["cardId"] = new string('x', 50000); return obj.ToJsonString(); },
    ["null card id"] = obj => { obj["cardId"] = null; return obj.ToJsonString(); },
    ["empty session id"] = obj => { obj["id"] = new string('0', 32); return obj.ToJsonString(); },
    ["negative remaining"] = obj => { obj["remainingSeconds"] = -1; return obj.ToJsonString(); },
    ["excessive remaining"] = obj => { obj["remainingSeconds"] = 600.01; return obj.ToJsonString(); },
    ["nonfinite remaining"] = obj => { obj["remainingSeconds"] = "NaN"; return obj.ToJsonString(); },
    ["numeric overflow"] = obj => obj.ToJsonString().Replace("600", "1e999", StringComparison.Ordinal),
    ["missing deadline"] = obj => { obj["deadline"] = null; return obj.ToJsonString(); },
    ["ancient deadline"] = obj => { obj["deadline"] = "0001-01-01T00:00:00Z"; return obj.ToJsonString(); },
    ["implausible deadline"] = obj => { obj["deadline"] = "9999-01-01T00:00:00Z"; return obj.ToJsonString(); },
    ["deadline before start"] = obj => { obj["deadline"] = epoch.AddDays(-1).ToString("O"); return obj.ToJsonString(); },
    ["paused with deadline"] = obj => { obj["paused"] = true; return obj.ToJsonString(); },
    ["pending with remainder"] = obj => { obj["completionPending"] = true; return obj.ToJsonString(); },
    ["array root"] = _ => "[]",
    ["oversized file"] = _ => new string(' ', 65536) + "null",
    ["trailing garbage"] = obj => obj.ToJsonString() + " garbage",
    ["trailing comma"] = obj => obj.ToJsonString()[..^1] + ",}",
    ["comment"] = obj => "/*no comments*/" + obj.ToJsonString()
};
foreach (var (name, mutate) in invalidJson)
{
    tests.Add(("Strict validation: " + name, directory =>
    {
        var store = Open(directory);
        store.Start("card", 10, epoch);
        string malformed = mutate(JsonNode.Parse(File.ReadAllText(Live(directory)))!.AsObject());
        byte[] bytes = Encoding.UTF8.GetBytes(malformed);
        File.WriteAllBytes(Live(directory), bytes);
        store = Open(directory);
        Check(store.Current is null, "Rejected input falls back to initial null backup.");
        Check(store.Warning is not null, "Rejected input has a warning.");
        Check(Directory.GetFiles(directory, "focus.json.corrupt-*").Any(path => File.ReadAllBytes(path).SequenceEqual(bytes)), "Rejected bytes preserved exactly.");
    }));
}

int failed = 0;
// Keep fixtures beside this console binary; some hosted environments deny the system temp directory.
string fixtureParent = Path.GetFullPath(AppContext.BaseDirectory);
string root = Path.Combine(fixtureParent, "Relay.FocusTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    for (int i = 0; i < tests.Count; i++)
    {
        var (name, run) = tests[i];
        string directory = Path.Combine(root, i.ToString("D3"));
        Directory.CreateDirectory(directory);
        try { run(directory); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + Environment.NewLine + ex); }
    }
}
finally
{
    // Delete only this runner's unique temporary tree, never an application data directory.
    if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(fixtureParent) && Path.GetFileName(root).StartsWith("Relay.FocusTests-", StringComparison.Ordinal))
        Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} focus tests passed.");
return failed == 0 ? 0 : 1;

static FocusSessionStore Open(string directory)
{
    var store = new FocusSessionStore(directory);
    store.Load();
    return store;
}
static string Live(string directory) => Path.Combine(directory, "focus.json");
static string Backup(string directory) => Live(directory) + ".bak";
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static void ThrowsDisk(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected IOException or UnauthorizedAccessException.");
}
