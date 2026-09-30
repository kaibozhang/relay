using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Relay.Core;

return Benchmark.Run(args);

internal static class Benchmark
{
    private const int Warmups = 3;
    private const double ThresholdMs = 500;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly List<Measurement> Measurements = [];
    private static long sampledManagedHighWatermark;
    private static long sampledWorkingSetHighWatermark;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException("Usage: dotnet run --project native/Relay.PerformanceTests -c Release (no arguments; synthetic fixtures only)");
            var root = FindWorkspace();
            var output = Path.Combine(root, "artifacts", "performance");
            RejectReparseAncestors(output);
            Directory.CreateDirectory(output);
            var started = DateTimeOffset.UtcNow;
            var runId = started.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var fixtures = Path.Combine(output, "fixtures", runId);
            RejectReparseAncestors(fixtures);
            Directory.CreateDirectory(fixtures);
            var provenance = new[] { "native/Relay.Core/Models.cs", "native/Relay.Core/VaultStore.cs", "native/Relay.Desktop/MainWindow.cs" }
                .ToDictionary(p => p, p => Hash(Path.Combine(root, p)));
            var coreAssemblyHash = Hash(typeof(VaultStore).Assembly.Location);
            var timer = Stopwatch.StartNew();
            var vaultSizes = new Dictionary<int, long>();
            Console.WriteLine($"PERFORMANCE synthetic-only run={runId}");
            Console.WriteLine("Actual VaultStore Clone/Save/Load; exact VisibleCards query mirror, NOT UI responsiveness.");
            Console.WriteLine("Warmups=3; samples=20 for 10/100/1000, 5 for 5000; p95 uses nearest rank (5 samples => maximum).");
            Console.WriteLine("Save includes production WriteThrough + Flush(true) + atomic replacement/backup; no mock I/O or cache purge.");
            Console.WriteLine("cards operation                      p50 ms    p95 ms    max ms  alloc max MiB  >500ms");
            foreach (var count in new[] { 10, 100, 1000, 5000 })
            {
                var directory = Path.Combine(fixtures, "cards-" + count);
                var store = new VaultStore(directory);
                store.Load();
                var document = CreateDocument(count, runId);
                VaultStore.Validate(document);
                store.Save(document);
                store.Save(document); // Establish a valid backup before all measured saves.
                var length = new FileInfo(store.DataPath).Length;
                Require(length < VaultStore.MaxUtf8Bytes, "Fixture exceeded the production 32 MiB limit.");
                vaultSizes.Add(count, length);
                var samples = count == 5000 ? 5 : 20;
                Measure(count, "clone", samples, () => VaultStore.Clone(document), value => VerifyDocument((VaultDocument)value, document));
                Measure(count, "save-durable", samples, () => { store.Save(document); return document; }, value => Require(ReferenceEquals(value, document), "Save fixture changed."));
                VerifyDocument(new VaultStore(directory).Load(), document);
                var reader = new VaultStore(directory);
                Measure(count, "load-warm-cache", samples, () => reader.Load(), value => VerifyDocument((VaultDocument)value, document));
                foreach (var search in new[]
                {
                    new SearchCase("visible-ready-empty", "ready", "", count - Enumerable.Range(0, count).Count(i => i % 5 is 2 or 3)),
                    new SearchCase("visible-all-empty", "all", "", count),
                    new SearchCase("search-common-title", "all", "  SYNTHETIC  ", count),
                    new SearchCase("search-body-hit", "all", "BoDy-MaRkEr", count),
                    new SearchCase("search-single-hit", "all", $"card-{count - 1:D6}", 1),
                    new SearchCase("search-no-hit", "all", "not-present-in-any-fixture", 0)
                })
                {
                    Measure(count, search.Name, samples,
                        () => VisibleCards(document, search.Filter, search.Query).ToList(),
                        value => VerifySearch((List<RelayCard>)value, search.ExpectedCount));
                }
                VerifyDocument(new VaultStore(directory).Load(), document);
                Require(!store.IsReadOnly && store.RecoveryNotice is null, "Unexpected recovery during benchmark.");
                Console.WriteLine($"fixture cards={count} bytes={length:N0} revision={document.Revision} validated");
            }
            timer.Stop();
            var sourceUnchanged = provenance.All(p => Hash(Path.Combine(root, p.Key)) == p.Value);
            var alerts = Measurements.Where(m => m.CardCount == 5000 && m.MaxMs > ThresholdMs)
                .Select(m => new { m.Operation, m.P50Ms, m.P95Ms, m.MaxMs, m.SamplesOver500Ms, m.MaxAllocatedBytes }).ToArray();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var report = new
            {
                schemaVersion = 1, status = sourceUnchanged ? "passed" : "source-changed-during-run", runId, startedAtUtc = started,
                finishedAtUtc = DateTimeOffset.UtcNow, durationSeconds = timer.Elapsed.TotalSeconds,
                environment = new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
                    serverGc = GCSettings.IsServerGC, gcLatencyMode = GCSettings.LatencyMode.ToString(),
                    stopwatchFrequency = Stopwatch.Frequency, stopwatchHighResolution = Stopwatch.IsHighResolution,
#if DEBUG
                    configuration = "Debug",
#else
                    configuration = "Release",
#endif
                    coreAssemblySha256 = coreAssemblyHash },
                scope = "Synthetic VaultStore and exact MainWindow.VisibleCards LINQ/string mirror only; no WPF rendering, UIA, input latency, startup, or UI responsiveness measurement.",
                methodology = new
                {
                    warmupsPerOperation = Warmups, samplesSmall = 20, samples5000 = 5,
                    percentile = "Nearest rank: sorted[ceil(p * sampleCount) - 1]; p95 at n=5 is the maximum, not a stable tail estimate.",
                    timing = "Stopwatch around one synchronous operation; fixture creation, correctness checks, GC sampling and console/report output excluded. No forced GC; natural collections included.",
                    allocation = "GC.GetAllocatedBytesForCurrentThread delta per synchronous operation; includes transient allocations, not retained memory or native allocations.",
                    memory = "Managed heap and working set sampled after each operation/check; not a continuous peak. Process PeakWorkingSet64 is OS-reported. Includes fixtures, validation and harness.",
                    io = "Real local disk VaultStore.Save with existing main+backup, stale-snapshot reads, validation, WriteThrough, Flush(true), and atomic replacement. This requests OS durable flush, not proof of physical hardware durability. Warm-cache load; cache not purged.",
                    search = "Copied MainWindow.VisibleCards filtering, concatenation, string.Join, OrdinalIgnoreCase Contains and full sorting, materialized with ToList as RenderList does before Take(200). No alternative search optimization.",
                    limitations = "Single sequential run on a shared host; other workloads may contend. No isolated CPU/frequency/cache controls. Small samples describe observations, not throughput guarantees.",
                    bodyCharacters = 200, referencesPerCard = 0, checkpointsPerCard = 0,
                    distribution = "Modulo 5: parked, active, done, future-snoozed parked, parked. Every seventh card pinned; deterministic dates and globally unique run-prefixed IDs.",
                    maxVaultBytes = VaultStore.MaxUtf8Bytes, fixtureRoot = Path.GetRelativePath(root, fixtures),
                    sourceUnchangedDuringRun = sourceUnchanged, sourceSha256 = provenance
                },
                vaultSizesBytes = vaultSizes,
                memory = new { sampledManagedHighWatermarkBytes = sampledManagedHighWatermark,
                    sampledWorkingSetHighWatermarkBytes = sampledWorkingSetHighWatermark, processPeakWorkingSetBytes = process.PeakWorkingSet64 },
                threshold5000 = new { milliseconds = ThresholdMs, criterion = "Any measured single operation strictly greater than 500 ms", optimizationReviewNeeded = alerts.Length != 0, operations = alerts },
                measurements = Measurements
            };
            var json = JsonSerializer.Serialize(report, Json);
            File.WriteAllText(Path.Combine(fixtures, "results.json"), json);
            File.WriteAllText(Path.Combine(output, "results.json"), json);
            Console.WriteLine($"5000-card >500ms operations: {(alerts.Length == 0 ? "none observed" : string.Join(", ", alerts.Select(a => a.Operation)))}");
            Console.WriteLine($"Measured duration={timer.Elapsed.TotalSeconds:F2}s; sampled managed high-watermark={sampledManagedHighWatermark / 1048576d:F2} MiB; process peak working set={process.PeakWorkingSet64 / 1048576d:F2} MiB");
            Console.WriteLine($"Report: {Path.Combine(output, "results.json")}");
            Require(sourceUnchanged, "Production source changed during benchmark; report is marked invalid for snapshot comparison.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Measure(int count, string name, int sampleCount, Func<object> operation, Action<object> verify)
    {
        for (var i = 0; i < Warmups; i++) verify(operation());
        var samples = new List<Sample>(sampleCount);
        using var process = Process.GetCurrentProcess();
        for (var i = 0; i < sampleCount; i++)
        {
            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var result = operation();
            var stop = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            samples.Add(new Sample(1000d * (stop - start) / Stopwatch.Frequency, allocated,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2));
            verify(result);
            sampledManagedHighWatermark = Math.Max(sampledManagedHighWatermark, GC.GetTotalMemory(false));
            process.Refresh();
            sampledWorkingSetHighWatermark = Math.Max(sampledWorkingSetHighWatermark, process.WorkingSet64);
            GC.KeepAlive(result);
        }
        var times = samples.Select(s => s.ElapsedMs).Order().ToArray();
        var allocations = samples.Select(s => (double)s.AllocatedBytes).Order().ToArray();
        var measurement = new Measurement(count, name, Warmups, sampleCount, Percentile(times, .5), Percentile(times, .95),
            times[0], times[^1], times.Average(), (long)Percentile(allocations, .5), (long)allocations[^1],
            samples.Sum(s => s.AllocatedBytes), samples.Count(s => s.ElapsedMs > ThresholdMs), samples);
        Measurements.Add(measurement);
        Console.WriteLine($"{count,5} {name,-29} {measurement.P50Ms,9:F3} {measurement.P95Ms,9:F3} {measurement.MaxMs,9:F3} {measurement.MaxAllocatedBytes / 1048576d,14:F3} {measurement.SamplesOver500Ms,7}");
    }

    // Deliberately mirror native/Relay.Desktop/MainWindow.cs VisibleCards, not an optimized substitute.
    // TextBox.Text is represented by searchText; materialization occurs in the caller as in RenderList.
    private static IEnumerable<RelayCard> VisibleCards(VaultDocument document, string filter, string searchText)
    {
        var now = DateTimeOffset.Now; var query = searchText.Trim();
        return document.Cards.Where(c => filter switch { "ready" => c.Status != "done" && (!c.SnoozedUntil.HasValue || c.SnoozedUntil <= now), "active" => c.Status == "active", "snoozed" => c.Status != "done" && c.SnoozedUntil > now, "done" => c.Status == "done", _ => true })
            .Where(c => query.Length == 0 || (c.Title + " " + c.NextAction + " " + c.Context + " " + string.Join(" ", c.Resources.Select(r => r.Label + " " + r.Target))).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Status == "active").ThenByDescending(c => c.UpdatedAt);
    }

    private static VaultDocument CreateDocument(int count, string runId)
    {
        var document = new VaultDocument { UpdatedAt = Epoch };
        for (var i = 0; i < count; i++)
        {
            document.Cards.Add(new RelayCard
            {
                Id = $"perf-{runId}-{count}-{i:D6}", Title = $"Synthetic card-{i:D6}",
                NextAction = $"Continue step {i:D6}", Context = new string('x', 189) + "body-marker",
                Status = i % 5 == 1 ? "active" : i % 5 == 2 ? "done" : "parked", Pinned = i % 7 == 0,
                CreatedAt = Epoch, UpdatedAt = Epoch.AddMinutes(i),
                CompletedAt = i % 5 == 2 ? Epoch.AddMinutes(i) : null,
                SnoozedUntil = i % 5 == 3 ? new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
                Resources = [], Checkpoints = []
            });
        }
        Require(document.Cards.All(c => c.Context.Length == 200), "Synthetic body must have 200 characters.");
        return document;
    }

    private static void VerifyDocument(VaultDocument actual, VaultDocument expected)
    {
        Require(actual.Cards.Count == expected.Cards.Count && actual.Revision == expected.Revision && actual.UpdatedAt == expected.UpdatedAt, "Vault roundtrip metadata mismatch.");
        for (var i = 0; i < actual.Cards.Count; i++)
        {
            var a = actual.Cards[i]; var e = expected.Cards[i];
            Require(a.Id == e.Id && a.Title == e.Title && a.Context == e.Context && a.NextAction == e.NextAction &&
                a.Status == e.Status && a.Pinned == e.Pinned && a.CreatedAt == e.CreatedAt && a.UpdatedAt == e.UpdatedAt &&
                a.SnoozedUntil == e.SnoozedUntil && a.CompletedAt == e.CompletedAt && a.Resources.Count == 0 && a.Checkpoints.Count == 0,
                "Vault roundtrip card mismatch.");
        }
    }

    private static void VerifySearch(List<RelayCard> cards, int expectedCount)
    {
        Require(cards.Count == expectedCount, "Search result count mismatch.");
        for (var i = 1; i < cards.Count; i++)
        {
            var previous = cards[i - 1]; var current = cards[i];
            var order = previous.Pinned.CompareTo(current.Pinned);
            if (order == 0) order = (previous.Status == "active").CompareTo(current.Status == "active");
            if (order == 0) order = previous.UpdatedAt.CompareTo(current.UpdatedAt);
            Require(order >= 0, "Search order mismatch.");
        }
    }

    private static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "native", "Relay.Core", "Relay.Core.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run within this repository.");
    }
    private static void RejectReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing redirected fixture/output directory: " + directory.FullName);
    }
    private sealed record SearchCase(string Name, string Filter, string Query, int ExpectedCount);
    private sealed record Sample(double ElapsedMs, long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections);
    private sealed record Measurement(int CardCount, string Operation, int WarmupCount, int SampleCount,
        double P50Ms, double P95Ms, double MinMs, double MaxMs, double MeanMs, long P50AllocatedBytes,
        long MaxAllocatedBytes, long TotalAllocatedBytes, int SamplesOver500Ms, List<Sample> Samples);
}
