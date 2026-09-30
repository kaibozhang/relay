using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Relay.Core;
using Relay.Desktop.Services;

return await Soak.Run(args);

internal sealed class Soak
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Random random;
    private readonly int seed;
    private readonly double minutes;
    private readonly string root, fixtures, vaultDir, focusDir;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, long> counts = new();
    private readonly Dictionary<string, List<double>> timings = new();
    private readonly Queue<string> trace = new();
    private VaultStore vault;
    private VaultDocument document;
    private long cycles, actions, nextId;
    private long peakManagedBytes, peakWorkingSetBytes;
    private double lastConsoleMinute = -1;
    private string stage = "initialize";

    private Soak(double minutes, int seed, string workspace)
    {
        this.minutes = minutes;
        this.seed = seed;
        random = new Random(seed);
        var artifacts = Path.Combine(workspace, "artifacts");
        var soak = Path.Combine(artifacts, "soak");
        RejectReparse(artifacts); RejectReparse(soak);
        Directory.CreateDirectory(soak);
        root = Path.Combine(soak, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        fixtures = Path.Combine(root, "fixtures");
        vaultDir = Path.Combine(fixtures, "vault");
        focusDir = Path.Combine(fixtures, "focus");
        Directory.CreateDirectory(vaultDir);
        Directory.CreateDirectory(focusDir);
        vault = new VaultStore(vaultDir);
        document = vault.Load();
    }

    public static async Task<int> Run(string[] args)
    {
        try
        {
            double minutes = 75;
            int seed = 170213;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--minutes" && ++i < args.Length)
                    minutes = double.Parse(args[i], CultureInfo.InvariantCulture);
                else if (args[i] == "--seed" && ++i < args.Length)
                    seed = args[i] == "deterministic" ? 170213 : int.Parse(args[i], CultureInfo.InvariantCulture);
                else throw new ArgumentException("Usage: Relay.Soak --minutes 75 --seed deterministic|INTEGER");
            }
            if (!double.IsFinite(minutes) || minutes <= 0 || minutes > 1440)
                throw new ArgumentException("Minutes must be finite and in (0, 1440].");
            var workspace = new DirectoryInfo(Environment.CurrentDirectory);
            while (workspace != null && !File.Exists(Path.Combine(workspace.FullName, "native", "Relay.Core", "Relay.Core.csproj")))
                workspace = workspace.Parent;
            if (workspace == null) throw new InvalidOperationException("Run from this repository or one of its directories.");
            return await new Soak(minutes, seed, workspace.FullName).Execute();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private async Task<int> Execute()
    {
        Console.WriteLine($"SOAK start seed={seed} minutes={minutes.ToString(CultureInfo.InvariantCulture)} root={root}");
        Console.WriteLine($"heartbeat={Path.Combine(root, "heartbeat.json")} summary={Path.Combine(root, "summary.json")}");
        try
        {
            do
            {
                cycles++;
                Measure("cycle", () =>
                {
                    for (int i = 0; i < 10; i++) MutateAndVerify();
                    stage = "focus lifecycle";
                    Measure("focusLifecycle", FocusLifecycle);
                    if ((cycles - 1) % 12 == 0)
                    {
                        stage = "vault corruption recovery"; Measure("vaultRecovery", CorruptVault);
                        stage = "vault locked write"; Measure("vaultLockedWrite", LockedVault);
                        stage = "vault denied write"; Measure("vaultDeniedWrite", DeniedVault);
                        stage = "two-store stale conflict"; Measure("staleConflict", StaleConflict);
                        stage = "focus locked and denied writes"; Measure("focusFaults", FocusFaults);
                        stage = "focus corruption recovery"; Measure("focusRecovery", CorruptFocus);
                    }
                });
                SampleMemory();
                WriteJson(Path.Combine(root, "heartbeat.json"), Report("running", null, false));
                if (Math.Floor(elapsed.Elapsed.TotalMinutes) > lastConsoleMinute)
                {
                    lastConsoleMinute = Math.Floor(elapsed.Elapsed.TotalMinutes);
                    Console.WriteLine($"SOAK {elapsed.Elapsed.TotalMinutes:F1}/{minutes:F1} min cycles={cycles} actions={actions} cards={document.Cards.Count} managedMiB={GC.GetTotalMemory(false) / 1048576.0:F1}");
                }
                // A real workload interval, not accelerated/synthetic elapsed time.
                // The last burst runs after the requested duration is reached.
                if (elapsed.Elapsed.TotalMinutes >= minutes) break;
                await Task.Delay(TimeSpan.FromSeconds(5));
            } while (true);
            stage = "final validation";
            Check(elapsed.Elapsed.TotalMinutes >= minutes, "Run stopped before requested real elapsed duration.");
            Compare(document, new VaultStore(vaultDir).Load(), "final fresh load");
            foreach (string required in new[] { "add", "edit", "checkpoint", "snooze", "pin", "resume", "state", "settings", "vaultRecovery", "vaultLockedWrite", "vaultDeniedWrite", "staleConflict", "focusComplete", "focusCancel", "focusRecovery", "focusLockedWrite", "focusDeniedWrite" })
                Check(counts.GetValueOrDefault(required) > 0, "Missing coverage: " + required);
            stage = "cleanup";
            CleanupFixtures();
            SampleMemory();
            var report = Report("passed", null, true);
            WriteJson(Path.Combine(root, "heartbeat.json"), report);
            WriteJson(Path.Combine(root, "summary.json"), report);
            PublishLatest(report);
            Console.WriteLine($"SOAK PASS elapsed={elapsed.Elapsed} cycles={cycles} actions={actions}; report={Path.Combine(root, "summary.json")}");
            return 0;
        }
        catch (Exception ex)
        {
            SampleMemory();
            var report = Report("failed", ex.ToString(), false);
            // Preserve all fixtures and recent steps at the first failure; never continue.
            WriteJson(Path.Combine(root, "repro.json"), new { seed, minutes, cycles, actions, stage, trace = trace.ToArray(), error = ex.ToString(), command = $"dotnet run --project native/Relay.Soak -c Release -- --minutes {minutes.ToString(CultureInfo.InvariantCulture)} --seed {seed}" });
            WriteJson(Path.Combine(root, "expected.json"), document);
            WriteJson(Path.Combine(root, "heartbeat.json"), report);
            WriteJson(Path.Combine(root, "summary.json"), report);
            PublishLatest(report);
            Console.Error.WriteLine($"SOAK FAIL stage={stage} cycle={cycles} action={actions}: {ex}\nRepro: {Path.Combine(root, "repro.json")}");
            return 1;
        }
    }

    private void MutateAndVerify()
    {
        actions++;
        // Guarantee each action in the smoke run, then use the seeded generator.
        int kind = actions <= 8 ? (int)(actions - 1) : random.Next(8);
        if (document.Cards.Count == 0) kind = 0;
        var now = Epoch.AddSeconds(actions);
        RelayCard? card = document.Cards.Count == 0 ? null : document.Cards[random.Next(document.Cards.Count)];
        string name;
        switch (kind)
        {
            case 0:
                name = "add";
                var id = "soak-card-" + ++nextId;
                var added = new RelayCard { Id = id, Title = $"Card {nextId} — 续页 🧪", CreatedAt = now, UpdatedAt = now, Context = new string('x', random.Next(40, 500)), NextAction = "Next\nstep\t" + actions };
                added.Resources.Add(new ResourceRef { Id = id + "-resource", Kind = "url", Target = "https://example.invalid/soak/" + nextId, Label = "Never opened" });
                if (document.Cards.Count == 250) document.Cards[random.Next(250)] = added;
                else document.Cards.Add(added);
                card = added;
                break;
            case 1: name = "edit"; card!.Title = $"Edited {actions} — 中文 🧪"; card.NextAction = "Continue " + random.Next(); card.Context = "Context\r\n" + new string('y', random.Next(20, 600)); break;
            case 2: name = "checkpoint"; AddCheckpoint(card!, now, "Checkpoint " + actions); break;
            case 3: name = "snooze"; card!.SnoozedUntil = random.Next(2) == 0 ? null : now.AddHours(random.Next(1, 72)); break;
            case 4: name = "pin"; card!.Pinned = !card.Pinned; break;
            case 5: name = "resume"; card!.Status = "active"; card.ResumeCount++; card.LastResumedAt = now; card.CompletedAt = null; card.SnoozedUntil = null; break;
            case 6: name = "state"; card!.Status = new[] { "parked", "active", "done" }[random.Next(3)]; card.CompletedAt = card.Status == "done" ? now : null; break;
            default: name = "settings"; document.Settings.FocusMinutes = random.Next(1, 181); document.Settings.CloseToTray = !document.Settings.CloseToTray; document.Settings.StartMinimized = !document.Settings.StartMinimized; break;
        }
        card!.UpdatedAt = now;
        stage = name;
        Count(name);
        Trace($"cycle={cycles} action={actions} {name} card={card.Id}");
        SaveAndVerify();
    }

    private void SaveAndVerify()
    {
        string before = Serialize(document);
        long revision = document.Revision;
        Measure("save", () => vault.Save(document));
        Check(document.Revision == revision + 1, "Revision must increase exactly once.");
        // Check Save changed metadata only, then compare a serialized detached expected clone.
        var expectedBefore = JsonSerializer.Deserialize<VaultDocument>(before)!;
        expectedBefore.Revision = document.Revision;
        expectedBefore.UpdatedAt = document.UpdatedAt;
        Compare(expectedBefore, document, "Save mutated content");
        string expected = Serialize(VaultStore.Clone(document));
        VaultDocument? actual = null;
        var fresh = new VaultStore(vaultDir);
        Measure("freshLoad", () => actual = fresh.Load());
        Check(!fresh.IsReadOnly && fresh.RecoveryNotice is null, "Normal fresh load unexpectedly recovered or became read-only.");
        Check(expected == Serialize(actual), "Fresh store differs from expected serialized document.");
        // Continue from the freshly opened store to exercise restart on every mutation.
        vault = fresh;
        document = actual!;
        Count("saveLoadCompare");
    }

    private void CorruptVault()
    {
        SaveAndVerify(); SaveAndVerify(); // backup and live now have identical card content.
        var backup = vault.Import(vault.DataPath + ".bak");
        byte[] backupBytes = File.ReadAllBytes(vault.DataPath + ".bak");
        byte[] bad = Encoding.UTF8.GetBytes("{broken-main-" + cycles + " 🧪");
        File.WriteAllBytes(vault.DataPath, bad);
        var recovered = new VaultStore(vaultDir);
        var loaded = recovered.Load();
        Check(!recovered.IsReadOnly && recovered.RecoveryNotice != null, "Good backup not recovered with notice.");
        Compare(backup, loaded, "backup recovery");
        Check(File.ReadAllBytes(vault.DataPath).SequenceEqual(bad), "Load overwrote corrupt main bytes.");
        Check(Directory.GetFiles(vaultDir, "vault.json.corrupt-*").Any(p => File.ReadAllBytes(p).SequenceEqual(bad)), "Missing byte-exact corruption evidence.");
        recovered.Save(loaded);
        Check(File.ReadAllBytes(vault.DataPath + ".bak").SequenceEqual(backupBytes), "Recovery rotated corrupt bytes onto backup.");
        Compare(loaded, new VaultStore(vaultDir).Load(), "saved recovery");
        vault = recovered; document = loaded;
        Count("vaultRecovery");
        CleanupEvidence(vaultDir);
    }

    private void LockedVault()
    {
        string before = Serialize(document);
        byte[] disk = File.ReadAllBytes(vault.DataPath);
        using (var locked = new FileStream(vault.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ExpectIo(() => vault.Save(document));
        Check(before == Serialize(document), "Locked Save mutated caller document.");
        Check(disk.SequenceEqual(File.ReadAllBytes(vault.DataPath)), "Locked Save changed committed bytes.");
        CheckNoTemps(vaultDir);
        SaveAndVerify(); Count("vaultLockedWrite");
    }

    private void DeniedVault()
    {
        string before = Serialize(document);
        byte[] disk = File.ReadAllBytes(vault.DataPath);
        var attributes = File.GetAttributes(vault.DataPath);
        try
        {
            File.SetAttributes(vault.DataPath, attributes | FileAttributes.ReadOnly);
            ExpectIo(() => vault.Save(document));
        }
        finally { File.SetAttributes(vault.DataPath, attributes); }
        Check(before == Serialize(document), "Denied Save mutated caller document.");
        Check(disk.SequenceEqual(File.ReadAllBytes(vault.DataPath)), "Denied Save changed committed bytes.");
        CheckNoTemps(vaultDir);
        SaveAndVerify(); Count("vaultDeniedWrite");
    }

    private void StaleConflict()
    {
        var first = new VaultStore(vaultDir); var firstDoc = first.Load();
        var stale = new VaultStore(vaultDir); var staleDoc = stale.Load();
        firstDoc.Settings.AlwaysOnTopStrip = !firstDoc.Settings.AlwaysOnTopStrip;
        first.Save(firstDoc);
        staleDoc.Cards[0].Title = "stale must not win";
        string before = Serialize(staleDoc);
        bool rejected = false;
        try { stale.Save(staleDoc); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Stale store write was accepted.");
        Check(before == Serialize(staleDoc), "Stale rejection mutated caller document.");
        Compare(firstDoc, new VaultStore(vaultDir).Load(), "Stale write changed winning document");
        vault = first; document = firstDoc; Count("staleConflict");
    }

    private void FocusLifecycle()
    {
        DateTimeOffset now = Epoch.AddDays(cycles % 10000);
        RelayCard card = document.Cards[random.Next(document.Cards.Count)];
        var focus = OpenFocus();
        Check(focus.Current is null, "Previous focus session leaked.");
        int duration = random.Next(1, 181);
        focus.Start(card.Id, duration, now);
        string id = focus.Current!.Id;
        Check(focus.Remaining(now.AddHours(-1)).TotalSeconds == duration * 60, "Rollback extended duration.");
        focus = RestartFocus(focus);
        double consumed = random.Next(1, duration * 60);
        focus.Pause(now.AddSeconds(consumed));
        double remaining = duration * 60 - consumed;
        Check(focus.Current!.Paused && focus.Current.Deadline is null, "Pause state invalid.");
        focus = RestartFocus(focus);
        Check(focus.Remaining(now.AddDays(20)).TotalSeconds == remaining, "Pause lost duration across restart.");
        focus.Pause(now.AddDays(20));
        DateTimeOffset resume = now.AddHours(2);
        focus.Resume(resume);
        focus = RestartFocus(focus);
        focus.Resume(resume.AddHours(1));
        Check(focus.Current!.Deadline == resume.AddSeconds(remaining), "Repeated Resume changed deadline.");
        Check(!focus.IsDue(resume.AddSeconds(remaining).AddTicks(-1)), "Focus completed early.");
        Check(focus.IsDue(resume.AddSeconds(remaining)), "Focus missed exact deadline.");
        focus.MarkCompletionPending(resume.AddSeconds(remaining));
        focus = RestartFocus(focus); // Simulate crash before writing the checkpoint.
        Check(focus.Current!.CompletionPending && focus.Current.Id == id, "Pending completion lost identity.");
        string marker = "Focus completed [focus:" + id + "]";
        CompleteOnce(focus, marker);
        focus = RestartFocus(focus); // Simulate crash after checkpoint commit, before acknowledgement.
        CompleteOnce(focus, marker);
        Check(document.Cards.Single(c => c.Id == card.Id).Checkpoints.Count(c => c.Text == marker) == 1, "Completion checkpoint duplicated after restart.");
        focus.MarkCompletionPending(now); focus.Pause(now); focus.Resume(now);
        focus.AcknowledgeCompletion(); focus.AcknowledgeCompletion();
        Check(OpenFocus().Current is null, "Acknowledged focus resurrected.");
        DeleteOwned(Path.Combine(focusDir, "focus.json"));
        Check(OpenFocus().Current is null, "Acknowledged focus resurrected from backup.");
        Count("focusComplete");
        focus = OpenFocus(); focus.Start(card.Id, duration, now); focus.Pause(now.AddSeconds(1));
        focus.Cancel();
        Check(OpenFocus().Current is null, "Cancelled focus survived restart.");
        DeleteOwned(Path.Combine(focusDir, "focus.json"));
        Check(OpenFocus().Current is null, "Cancelled focus resurrected from backup.");
        focus = OpenFocus(); focus.Cancel();
        Count("focusCancel");
    }

    private void CompleteOnce(FocusSessionStore focus, string marker)
    {
        var fresh = new VaultStore(vaultDir); document = fresh.Load(); vault = fresh;
        var card = document.Cards.Single(c => c.Id == focus.Current!.CardId);
        if (!card.Checkpoints.Any(c => c.Text == marker))
        {
            AddCheckpoint(card, Epoch.AddSeconds(actions), marker);
            SaveAndVerify();
        }
    }

    private void FocusFaults()
    {
        var focus = OpenFocus(); var now = Epoch.AddDays(cycles % 10000);
        focus.Start(document.Cards[0].Id, 5, now);
        string path = Path.Combine(focusDir, "focus.json");
        string before = Serialize(focus.Current);
        byte[] disk = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ExpectIo(() => focus.Pause(now.AddSeconds(10)));
        Check(before == Serialize(focus.Current), "Locked focus write mutated Current.");
        Check(disk.SequenceEqual(File.ReadAllBytes(path)), "Locked focus write changed disk.");
        CheckNoTemps(focusDir); Count("focusLockedWrite");
        var attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            ExpectIo(() => focus.Cancel());
        }
        finally { File.SetAttributes(path, attributes); }
        Check(before == Serialize(focus.Current), "Denied focus write mutated Current.");
        Check(disk.SequenceEqual(File.ReadAllBytes(path)), "Denied focus write changed live disk.");
        CompareFocus(focus.Current, OpenFocus().Current, "Failed cancellation lost committed session.");
        CheckNoTemps(focusDir); Count("focusDeniedWrite");
        focus.Cancel();
    }

    private void CorruptFocus()
    {
        var focus = OpenFocus(); var now = Epoch.AddDays(cycles % 10000);
        focus.Start(document.Cards[0].Id, 3, now);
        string expected = Serialize(focus.Current);
        focus.Pause(now.AddSeconds(30)); // backup is the running session.
        string path = Path.Combine(focusDir, "focus.json");
        byte[] bad = Encoding.UTF8.GetBytes("{bad-focus-" + cycles);
        File.WriteAllBytes(path, bad);
        focus = OpenFocus();
        Check(expected == Serialize(focus.Current) && focus.Warning != null, "Focus did not recover good backup.");
        Check(File.ReadAllBytes(path).SequenceEqual(bad), "Focus Load overwrote corrupt main.");
        Check(Directory.GetFiles(focusDir, "focus.json.corrupt-*").Any(p => File.ReadAllBytes(p).SequenceEqual(bad)), "Focus corrupt bytes not preserved.");
        focus.Cancel();
        File.WriteAllBytes(path, bad);
        Check(OpenFocus().Current is null, "Corrupt live resurrected cancelled focus.");
        focus = OpenFocus(); focus.Cancel();
        CleanupEvidence(focusDir); Count("focusRecovery");
    }

    private FocusSessionStore OpenFocus() { var focus = new FocusSessionStore(focusDir); focus.Load(); return focus; }
    private FocusSessionStore RestartFocus(FocusSessionStore original)
    {
        var fresh = OpenFocus(); CompareFocus(original.Current, fresh.Current, "Focus restart changed snapshot."); Count("focusRestartCompare"); return fresh;
    }
    private static void AddCheckpoint(RelayCard card, DateTimeOffset now, string text)
    {
        if (card.Checkpoints.Count >= 32) card.Checkpoints.RemoveAt(0);
        card.Checkpoints.Add(new Checkpoint { At = now, Text = text });
    }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static void Compare(VaultDocument expected, VaultDocument actual, string message) => Check(Serialize(expected) == Serialize(actual), message);
    private static void CompareFocus(FocusSession? expected, FocusSession? actual, string message) => Check(Serialize(expected) == Serialize(actual), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("INVARIANT: " + message); }
    private static void ExpectIo(Action operation)
    {
        bool rejected = false;
        try { operation(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        Check(rejected, "Fault injection did not reject the write.");
    }
    private void Count(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
    private void Trace(string message) { if (trace.Count == 100) trace.Dequeue(); trace.Enqueue(message); }
    private void Measure(string name, Action operation)
    {
        var watch = Stopwatch.StartNew();
        try { operation(); }
        finally
        {
            if (!timings.TryGetValue(name, out var values)) timings[name] = values = new();
            values.Add(watch.Elapsed.TotalMilliseconds);
        }
    }
    private static object Distribution(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, min = sorted[0], mean = sorted.Average(), p50 = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99), max = sorted[^1] };
    }
    private void SampleMemory()
    {
        peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(false));
        using var process = Process.GetCurrentProcess();
        peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
    }
    private object Report(string status, string? error, bool cleaned) => new
    {
        status, seed, requestedMinutes = minutes, startedUtc = started, observedUtc = DateTimeOffset.UtcNow,
        elapsedSeconds = elapsed.Elapsed.TotalSeconds, clock = "Stopwatch monotonic real elapsed; Task.Delay(5 seconds) between workload bursts",
        processId = Environment.ProcessId, cycles, actions, cardCount = document.Cards.Count, stage,
        counters = counts, milliseconds = timings.ToDictionary(p => p.Key, p => Distribution(p.Value)),
        memory = new { managedBytes = GC.GetTotalMemory(false), peakManagedBytes, peakWorkingSetBytes, gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
        fixtureBytes = Directory.Exists(fixtures) ? Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0,
        fixturesCleaned = cleaned, runDirectory = root, reportPreserved = true, error,
        determinism = "Seed controls action choices, card IDs and synthetic timestamps; store-generated focus GUIDs and commit UTC metadata are intentionally real. Repro records recent actions.",
        scope = "Synthetic repository-local fixtures only. No UI, user data, external resources, or network access."
    };
    private void PublishLatest(object report)
    {
        // The requested latest-summary pointer is the sole write outside this run's unique directory.
        string latest = Path.Combine(Path.GetDirectoryName(root)!, "latest-summary.json");
        RejectReparse(latest);
        string staging = Path.Combine(root, "latest-summary.staging.json");
        WriteJson(staging, report);
        File.Move(staging, latest, overwrite: true);
    }
    private static void WriteJson(string path, object value)
    {
        RejectReparse(path);
        string temp = path + ".tmp";
        RejectReparse(temp);
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing reparse point: " + path);
    }
    private void AssertOwned(string path)
    {
        string full = Path.GetFullPath(path);
        Check(full.StartsWith(Path.GetFullPath(fixtures) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Cleanup target escaped fixtures: " + full);
        for (var current = new FileInfo(full).Directory; current != null && current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = current.Parent)
            RejectReparse(current.FullName);
        RejectReparse(full);
    }
    private void DeleteOwned(string path) { AssertOwned(path); File.Delete(path); }
    private void CleanupEvidence(string directory)
    {
        foreach (string path in Directory.GetFiles(directory, "*.corrupt-*")) DeleteOwned(path);
    }
    private static void CheckNoTemps(string directory) => Check(!Directory.GetFiles(directory).Any(p => Path.GetFileName(p).Contains(".tmp", StringComparison.Ordinal)), "Failed write leaked staging file.");
    private void CleanupFixtures()
    {
        Check(Path.GetFullPath(fixtures) == Path.Combine(Path.GetFullPath(root), "fixtures"), "Unexpected fixture cleanup root.");
        RejectReparse(fixtures);
        foreach (string path in Directory.GetFiles(fixtures, "*", SearchOption.AllDirectories)) AssertOwned(path);
        foreach (string path in Directory.GetDirectories(fixtures, "*", SearchOption.AllDirectories)) { AssertOwned(path); RejectReparse(path); }
        Directory.Delete(fixtures, recursive: true);
    }
}
