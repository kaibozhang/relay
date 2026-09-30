using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

// Run from repository root: dotnet run --project native/Relay.PackageTests -c Release
// No production references. Only the extracted Release exe is driven through owned-PID UIA.
// Retain all evidence; no clipboard, screenshots, registry, global input, or resource launches.
internal static class Program
{
    private sealed record Result(string name, string status, long durationMs, string? detail);
    private sealed record LaunchRecord(int pid, string[] arguments);
    private static readonly object Gate = new();
    private static readonly List<Process> Owned = [];
    private static readonly List<Result> Results = [];
    private static readonly List<LaunchRecord> Launches = [];
    private static readonly List<string> CleanupErrors = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static string _root = "", _run = "", _package = "", _exe = "", _portable = "", _working = "";
    private static string _zipHash = "", _exeHash = "", _dllHash = "", _version = "", _runtimePath = "", _portableId = "";
    private static bool _timedOut, _stopping;
    private static int _reportWritten;
    private const string PortableTitle = "Package portable · 续页";
    private const string PortableNext = "Restart the packaged executable and find this next action";
    private const string Context = "Synthetic package-test evidence; no personal data.";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _root = Path.GetFullPath(Environment.CurrentDirectory);
        if (args.Length != 0 || !File.Exists(Path.Combine(_root, "native", "Relay.Desktop", "Relay.Desktop.csproj")))
        {
            Console.Error.WriteLine("Run without arguments from repository root; refusing to guess paths.");
            return 2;
        }
        _run = Path.Combine(_root, "artifacts", "package-tests", Guid.NewGuid().ToString("N"));
        AssertNoReparse(_run);
        Directory.CreateDirectory(_run);
        _package = Path.Combine(_run, "package");
        _exe = Path.Combine(_package, "Relay.exe");
        _portable = Path.Combine(_package, "Data");
        _working = Path.Combine(_run, "working");
        Directory.CreateDirectory(_working);
        Directory.CreateDirectory(Path.Combine(_run, "empty-dotnet"));
        Console.WriteLine("Evidence: " + _run);
        // Independent watchdog survives blocked UIA/COM calls; reserve ten seconds for cleanup.
        using var watchdog = new System.Threading.Timer(_ => Deadline(), null, 170_000, Timeout.Infinite);
        try
        {
            Require(OperatingSystem.IsWindows() && Environment.UserInteractive, "Interactive Windows desktop required.");
            var desktop = Native.OpenInputDesktop(0, false, 1);
            Require(desktop != IntPtr.Zero, "Input desktop unavailable; UIA tests were not run.");
            Native.CloseDesktop(desktop);
            if (!Test("package.trusted-zip-extract-and-sha256", PreparePackage))
                throw new InvalidOperationException("Package preparation failed; refusing launch.");
            Test("cli.reject-quit-without-default-data-mutation", () => InvalidCli("--quit"));
            Test("cli.reject-missing-vault-without-default-data-mutation", () => InvalidCli("--vault"));
            Test("portable.capture-save-to-sibling-data", PortableSave);
            Test("portable.restart-reads-existing-ui", PortableRestart);
            Test("explicit.unicode-vault-and-sendto-two-references", ExplicitSendTo);
            Test("portable.cold-capture-keeps-main-hidden", ColdCapture);
        }
        catch (Exception error) { Record("suite", "failed", 0, error.ToString()); }
        finally
        {
            lock (Gate) _stopping = true;
            StopAll();
            WriteReport();
            foreach (var process in Owned) process.Dispose();
        }
        return _timedOut ? 124 : Results.Any(r => r.status != "passed") || CleanupErrors.Count > 0 ? 1 : 0;
    }

    private static void PreparePackage()
    {
        var zipPath = Path.Combine(_root, "dist", "Relay-Windows-x64.zip");
        AssertNoReparse(zipPath);
        using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _zipHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var expected = File.ReadAllText(zipPath + ".sha256").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        Require(string.Equals(_zipHash, expected, StringComparison.OrdinalIgnoreCase), "ZIP differs from SHA256 sidecar.");
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            Require(archive.Entries.Count is > 0 and <= 5000, "Unreasonable archive entry count.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                Require(name.Length > 0 && !Path.IsPathRooted(name) && !name.Contains(':') && !name.Contains((char)92), "Unsafe ZIP path.");
                var segments = name.TrimEnd('/').Split('/');
                Require(segments.All(s => s.Length > 0 && s != "." && s != ".." && !s.EndsWith('.') && !s.EndsWith(' ')), "ZIP traversal/ambiguous path.");
                Require(!segments[0].Equals("Data", StringComparison.OrdinalIgnoreCase), "Package must not contain pre-existing Data.");
                var target = Path.GetFullPath(Path.Combine(_package, name));
                Require(target.StartsWith(_package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && seen.Add(target), "ZIP escape/duplicate.");
                Require(((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000, "ZIP symlinks forbidden.");
                bytes += entry.Length;
                Require(entry.Length <= 536870912 && bytes <= 1073741824, "Unreasonable archive size.");
            }
        }
        stream.Position = 0;
        // Same read-locked stream was hashed and validated: no replacement race.
        ZipFile.ExtractToDirectory(stream, _package, overwriteFiles: false);
        Require(File.Exists(_exe) && File.Exists(Path.Combine(_package, "portable.txt")), "Portable exe/marker missing.");
        Require(!Directory.Exists(_portable), "Extraction unexpectedly contains Data.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_package, "release-manifest.json")));
        var m = manifest.RootElement;
        Require(m.GetProperty("selfContained").GetBoolean() && m.GetProperty("portable").GetBoolean() &&
                m.GetProperty("runtime").GetString() == "win-x64", "Expected self-contained portable win-x64 release.");
        _version = m.GetProperty("version").GetString() ?? "";
        _exeHash = Hash(_exe);
        _dllHash = Hash(Path.Combine(_package, "Relay.dll"));
        Require(_exeHash == Hash(Path.Combine(_root, "dist", "windows", "Relay.exe")), "ZIP executable differs from dist/windows.");
        Require(_dllHash == Hash(Path.Combine(_root, "dist", "windows", "Relay.dll")), "ZIP application DLL differs from dist/windows.");
        foreach (var name in new[] { "Relay.exe", "Relay.dll", "Relay.Core.dll", "coreclr.dll", "portable.txt" })
        {
            var file = m.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
            Require(string.Equals(Hash(Path.Combine(_package, name)), file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Manifest mismatch: " + name);
        }
    }

    private static void InvalidCli(string argument)
    {
        var before = Snapshot(_portable);
        var process = Launch(argument);
        AutomationElement? dialog = null;
        Await(() =>
        {
            Require(FindWindow(process, "main.capture") is null, "Invalid CLI displayed dashboard.");
            dialog = Windows(process).FirstOrDefault(w => Text(w).Contains("续页未能启动", StringComparison.Ordinal));
            return dialog != null;
        }, "owned startup-error dialog");
        var message = Text(dialog!);
        Require(argument == "--quit" ? message.Contains("Unsupported Relay argument", StringComparison.Ordinal) :
            message.Contains("--vault 后需要", StringComparison.Ordinal), "Expected CLI validation error: " + message);
        Require(before == Snapshot(_portable), "Invalid CLI mutated default Data before dismissal.");
        EnsureOwned(process, dialog!);
        var hwnd = (IntPtr)dialog!.Current.NativeWindowHandle;
        Require(hwnd != IntPtr.Zero && Native.GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == process.Id, "Dialog HWND not owned.");
        Require(Native.PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero), "Owned dialog WM_CLOSE failed.");
        Await(() => process.HasExited, "invalid CLI exits after dialog close");
        Require(before == Snapshot(_portable), "Invalid CLI mutated default Data after exit.");
        Require(!Directory.Exists(Path.Combine(_working, "Data")), "CLI created working-directory Data.");
    }

    private static void PortableSave()
    {
        Require(!Directory.Exists(_portable), "Portable save must start with absent Data.");
        var process = Launch();
        var main = WindowWith(process, "main.capture");
        Require(main.Current.FrameworkId == "WPF", "Dashboard must be native WPF.");
        Element(main, "main.search");
        Require(Directory.Exists(_portable), "Portable launch did not create sibling Data.");
        Require(Cards(_portable).Length == 0, "New portable vault not empty.");
        process.Refresh();
        var core = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
        Require(core != null && Path.GetFullPath(core.FileName).Equals(Path.Combine(_package, "coreclr.dll"), StringComparison.OrdinalIgnoreCase), "Package did not load own coreclr.");
        _runtimePath = core!.FileName;
        Invoke(process, main, "main.capture");
        Save(process, WindowWith(process, "capture.next"), PortableTitle, PortableNext);
        Await(() => Cards(_portable).Length == 1, "portable vault.json commit");
        var card = Cards(_portable).Single();
        CheckCard(card, PortableTitle, PortableNext, 0);
        _portableId = card.GetProperty("id").GetString()!;
        Require(!Directory.Exists(Path.Combine(_working, "Data")), "Portable Data was relative to working directory.");
        Stop(process); // Deliberately abrupt owned-only shutdown tests committed persistence.
    }

    private static void PortableRestart()
    {
        Require(_portableId.Length > 0, "Prerequisite portable save failed.");
        var hash = Hash(Path.Combine(_portable, "vault.json"));
        var process = Launch();
        var main = WindowWith(process, "main.search");
        Set(process, main, "main.search", PortableTitle);
        AutomationElement? card = null;
        Await(() => (card = Find(main, "card." + _portableId)) != null, "persisted card in restarted UI");
        Require(Text(card!).Contains(PortableTitle, StringComparison.Ordinal), "Restarted card UI omitted title.");
        Require(Text(main).Contains(PortableNext, StringComparison.Ordinal), "Restarted UI omitted next action.");
        CheckCard(Cards(_portable).Single(), PortableTitle, PortableNext, 0);
        Require(hash == Hash(Path.Combine(_portable, "vault.json")), "Read-only restart/search rewrote vault.json.");
    }

    private static void ExplicitSendTo()
    {
        string before = "";
        // Windows can release file locks just after the killed process handle is signaled.
        Await(() => { before = Snapshot(_portable); return true; }, "portable handles released after restart shutdown");
        var vault = Path.Combine(_run, "custom vault 空间", "资料 续页");
        Directory.CreateDirectory(vault);
        Require(!Directory.EnumerateFileSystemEntries(vault).Any(), "Explicit vault not empty.");
        var fixtureDir = Path.Combine(_run, "fixtures only");
        Directory.CreateDirectory(fixtureDir);
        var paths = new[] { Path.Combine(fixtureDir, "first 空格.txt"), Path.Combine(fixtureDir, "second 引用.txt") };
        var sentinel = "PACKAGE_TEST_CONTENT_MUST_NOT_BE_READ_" + Guid.NewGuid().ToString("N");
        foreach (var path in paths) File.WriteAllText(path, sentinel, Encoding.UTF8);
        // Exclusive handles allow metadata checks but prevent content reads/copies by the app.
        using var locked1 = new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var locked2 = new FileStream(paths[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var owner = Launch("--vault", vault);
        var main = WindowWith(owner, "main.capture");
        Element(main, "main.search");
        Require(Cards(vault).Length == 0, "Explicit vault loaded portable cards.");
        var second = Launch("--vault", vault, "--capture", "--sendto", paths[0], paths[1]);
        Await(() => second.HasExited, "second process acknowledges forwarding and exits");
        Require(second.ExitCode == 0, "Forwarding process failed: " + second.ExitCode);
        var capture = WindowWith(owner, "capture.next");
        Await(() => paths.All(p => Text(capture).Contains(Path.GetFileName(p), StringComparison.Ordinal)), "both forwarded resource labels");
        Require(FindAll(capture, "capture.removeResource").Length == 2, "--sendto must produce exactly two rows.");
        Require(!Text(capture).Contains(sentinel, StringComparison.Ordinal), "UI exposed fixture content.");
        const string title = "Explicit vault · 两个引用";
        const string next = "Confirm both path-only references survived forwarding";
        Save(owner, capture, title, next);
        Await(() => Cards(vault).Length == 1, "explicit vault.json commit");
        var card = Cards(vault).Single();
        CheckCard(card, title, next, 2);
        var refs = card.GetProperty("resources").EnumerateArray().ToArray();
        foreach (var path in paths)
        {
            var resource = refs.Single(r => r.GetProperty("target").GetString() == path);
            Require(resource.GetProperty("label").GetString() == Path.GetFileName(path) && resource.GetProperty("kind").GetString() == "file", "Resource path/label/kind mismatch.");
        }
        Require(!File.ReadAllText(Path.Combine(vault, "vault.json")).Contains(sentinel, StringComparison.Ordinal), "Vault copied fixture content.");
        Stop(owner);
        Require(before == Snapshot(_portable), "Explicit --vault mutated portable Data.");
        Require(!Directory.Exists(Path.Combine(_working, "Data")), "Explicit launch created working-directory Data.");
    }

    private static void ColdCapture()
    {
        var before = Hash(Path.Combine(_portable, "vault.json"));
        var process = Launch("--capture");
        var capture = WindowWith(process, "capture.next");
        Require(capture.Current.FrameworkId == "WPF" && !capture.Current.IsOffscreen, "Cold capture not visible native WPF.");
        for (var i = 0; i < 8; i++)
        {
            var main = FindWindow(process, "main.capture");
            Require(main == null || main.Current.IsOffscreen, "Cold --capture exposed dashboard.");
            Thread.Sleep(100); // Bounded stability sample of owned UI, not job polling.
        }
        Invoke(process, capture, "capture.cancel");
        Await(() => FindWindow(process, "capture.next") == null, "cold capture cancellation");
        var remaining = FindWindow(process, "main.capture");
        Require(remaining == null || remaining.Current.IsOffscreen, "Cancelling cold capture exposed dashboard.");
        Require(before == Hash(Path.Combine(_portable, "vault.json")), "Cancelled cold capture modified vault.");
    }

    private static Process Launch(params string[] args)
    {
        var info = new ProcessStartInfo(_exe) { UseShellExecute = false, WorkingDirectory = _working };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT(x86)" }) info.Environment[name] = Path.Combine(_run, "empty-dotnet");
        info.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE" }) info.Environment.Remove(name);
        lock (Gate)
        {
            Require(!_stopping, "Suite stopping; no new processes allowed.");
            var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            Owned.Add(process);
            Launches.Add(new(process.Id, args));
            Console.WriteLine($"Owned PID {process.Id}: {string.Join(' ', args)}");
            return process;
        }
    }
    private static void Save(Process process, AutomationElement capture, string title, string next)
    {
        Set(process, capture, "capture.title", title);
        Set(process, capture, "capture.next", next);
        Set(process, capture, "capture.context", Context);
        Invoke(process, capture, "capture.save");
        Await(() => FindWindow(process, "capture.next") == null, "capture closes after save");
    }
    private static void CheckCard(JsonElement card, string title, string next, int resources)
    {
        Require(card.GetProperty("title").GetString() == title && card.GetProperty("nextAction").GetString() == next &&
            card.GetProperty("context").GetString() == Context && card.GetProperty("resources").GetArrayLength() == resources,
            "Persisted fields/resource count differ from UI input.");
    }
    private static JsonElement[] Cards(string vault)
    {
        var file = Path.Combine(vault, "vault.json");
        if (!File.Exists(file)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.Clone()).ToArray();
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string Snapshot(string directory)
    {
        if (!Directory.Exists(directory)) return "ABSENT";
        return string.Join(Environment.NewLine, Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Directory.Exists(p) ? "D:" + p :
                "F:" + p + ":" + File.GetLastWriteTimeUtc(p).Ticks + ":" + Hash(p)));
    }
    private static void AssertNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) || File.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Refusing redirected test path: " + current);
    }
    private static AutomationElement[] Windows(Process process)
    {
        if (process.HasExited) return [];
        return AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)).Cast<AutomationElement>().ToArray();
    }
    private static AutomationElement? FindWindow(Process process, string id)
    {
        foreach (var root in Windows(process))
        {
            var match = Find(root, id);
            while (match != null && match.Current.ProcessId == process.Id)
            {
                if (match.Current.ControlType == ControlType.Window && match.Current.NativeWindowHandle != 0) return match;
                match = TreeWalker.ControlViewWalker.GetParent(match);
            }
        }
        return null;
    }
    private static AutomationElement WindowWith(Process process, string id)
    {
        AutomationElement? result = null;
        Await(() => { Require(!process.HasExited, "Owned process exited before UI."); return (result = FindWindow(process, id)) != null; }, "owned window " + id);
        return result!;
    }
    private static AutomationElement? Find(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static AutomationElement[] FindAll(AutomationElement root, string id) => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)).Cast<AutomationElement>().ToArray();
    private static AutomationElement Element(AutomationElement root, string id)
    {
        AutomationElement? result = null;
        Await(() => (result = Find(root, id)) != null, "control " + id);
        return result!;
    }
    private static string Text(AutomationElement root) => root.Current.Name + Environment.NewLine + string.Join(Environment.NewLine,
        root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
    private static void EnsureOwned(Process process, AutomationElement element)
    {
        lock (Gate) Require(Owned.Contains(process) && !process.HasExited && element.Current.ProcessId == process.Id, "Refusing UI operation outside live owned PID.");
    }
    private static void Invoke(Process process, AutomationElement root, string id)
    {
        var element = Element(root, id); EnsureOwned(process, element);
        Require(element.Current.IsEnabled, "Disabled control: " + id);
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static void Set(Process process, AutomationElement root, string id, string value)
    {
        var element = Element(root, id); EnsureOwned(process, element);
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    }
    private static void Await(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew(); Exception? last = null;
        while (watch.ElapsedMilliseconds < 12_000 && Clock.ElapsedMilliseconds < 165_000)
        {
            try { if (condition()) return; }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or IOException or JsonException) { last = error; }
            Thread.Sleep(75);
        }
        throw new TimeoutException("Timed out: " + description + (last == null ? "" : "; " + last.Message));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static bool Test(string name, Action body)
    {
        var watch = Stopwatch.StartNew();
        try { body(); Record(name, "passed", watch.ElapsedMilliseconds, null); return true; }
        catch (Exception error) { Record(name, "failed", watch.ElapsedMilliseconds, error.ToString()); return false; }
        finally { StopAll(); }
    }
    private static void Record(string name, string status, long ms, string? detail)
    {
        lock (Gate) Results.Add(new(name, status, ms, detail));
        Console.WriteLine($"{status.ToUpperInvariant()} {name} ({ms} ms): {detail}");
    }
    private static void Stop(Process process)
    {
        lock (Gate) Require(Owned.Contains(process), "Refusing termination of unowned process.");
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: false);
            Require(process.WaitForExit(3000), "Owned PID did not stop: " + process.Id);
        }
    }
    private static void StopAll()
    {
        Process[] processes; lock (Gate) processes = Owned.ToArray();
        // Stop by handles we started, never by name and never an entire tree.
        foreach (var p in processes)
            try { if (!p.HasExited) p.Kill(entireProcessTree: false); }
            catch (Exception error) { lock (Gate) CleanupErrors.Add($"PID {p.Id}: {error.Message}"); }
        var watch = Stopwatch.StartNew();
        foreach (var p in processes)
            try { if (!p.HasExited && !p.WaitForExit(Math.Max(1, 4000 - (int)watch.ElapsedMilliseconds))) lock (Gate) CleanupErrors.Add("PID still running: " + p.Id); }
            catch (Exception error) { lock (Gate) CleanupErrors.Add($"PID {p.Id}: {error.Message}"); }
    }
    private static void Deadline()
    {
        _timedOut = true;
        lock (Gate) _stopping = true;
        Record("suite.deadline", "failed", Clock.ElapsedMilliseconds, "170-second deadline exceeded; owned-only cleanup before three-minute bound.");
        StopAll();
        WriteReport();
        Environment.Exit(124);
    }
    private static void WriteReport()
    {
        if (Interlocked.Exchange(ref _reportWritten, 1) != 0) return;
        lock (Gate)
        {
            var report = new
            {
                schemaVersion = 1, startedUtc = Started, finishedUtc = DateTimeOffset.UtcNow, durationMs = Clock.ElapsedMilliseconds,
                status = _timedOut ? "timed-out" : Results.Any(r => r.status != "passed") || CleanupErrors.Count > 0 ? "failed" : "passed",
                passed = Results.Count(r => r.status == "passed"), failed = Results.Count(r => r.status != "passed"),
                runDirectory = _run, packageDirectory = _package, zipPath = Path.Combine(_root, "dist", "Relay-Windows-x64.zip"),
                zipSha256 = _zipHash, exeSha256 = _exeHash, appDllSha256 = _dllHash, packageVersion = _version,
                loadedCoreClr = _runtimePath, isolatedPath = true, isolatedDotnetRoot = true, cleanVmVerified = false,
                tests = Results.ToArray(), launches = Launches.ToArray(),
                ownedProcesses = Owned.Select(p => new { pid = p.Id, exited = p.HasExited, exitCode = p.HasExited ? (int?)p.ExitCode : null }).ToArray(),
                cleanupErrors = CleanupErrors.ToArray(),
                limitations = new[] {
                    "Not a clean-VM guarantee; PATH/DOTNET_ROOT isolation plus package-local coreclr verified.",
                    "No general user-directory scan or machine-wide file/registry audit; own Data and working directory only.",
                    "Exclusive fixture handles prohibit successful content reads; no system-wide attempted-read tracing.",
                    "Cold capture visibility sampled through owned UIA, not compositor tracing."
                }
            };
            var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_run, "report.json"), text, Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "artifacts", "package-tests", "latest.json"), text, Encoding.UTF8);
            Console.WriteLine($"RESULT {report.status}: {report.passed} passed, {report.failed} failed. ZIP SHA256 {_zipHash}");
        }
    }
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
