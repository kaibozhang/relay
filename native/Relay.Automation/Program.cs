using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Relay.Automation;

internal static class Program
{
    private static readonly List<Process> Owned = [];
    private sealed record TestResult(string name, string status, long durationMs, string? detail);
    private static readonly List<TestResult> Results = [];
    private static bool _independentDeliveryMissing;
    private static bool EnvironmentCorroborated => _independentDeliveryMissing && Results.Any(r => r.name == "global-hotkey.ctrl-alt-space" && r.status == "failed");
    private static int EnvironmentFailures => EnvironmentCorroborated ? Results.Count(r => r.status == "failed" && r.name is "global-hotkey.ctrl-alt-space" or "hotkey.independent-delivery-probe") : 0;
    private static readonly List<object> Layouts = [];
    private static readonly List<object> Screenshots = [];
    private static readonly List<object> Metrics = [];
    private static readonly Dictionary<int, string> DiagnosticDirectories = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Stopwatch Session = Stopwatch.StartNew();
    private static string _root = "", _output = "", _vault = "", _exe = "";
    private static Process? _app;
    private static int _failures;
    private static int _stressRestarts;
    private static bool _release;
    private static bool _hotkeyAvailable;
    private const int HotkeyId = 0x6A51;
    private const int WaitMs = 10000;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            _root = Workspace(args);
            _exe = _release ? Path.Combine(_root,"dist","windows","Relay.exe") : Path.Combine(_root, "native", "Relay.Desktop", "bin", "Debug", "net10.0-windows", "Relay.exe");
            if (!File.Exists(_exe)) throw new FileNotFoundException("Build or publish the selected native Relay executable before testing.", _exe);
            var run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            _output = Path.Combine(_root, "artifacts", "native-tests", run);
            _vault = Path.Combine(_root, "artifacts", "test-vault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_output);
            Directory.CreateDirectory(_vault);
            // Probe before launching: never send a global hotkey known to belong to another app.
            _hotkeyAvailable = Native.RegisterHotKey(IntPtr.Zero, HotkeyId, 0x4003, 0x20);
            if (_hotkeyAvailable) Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            Test("startup.initial-empty", () =>
            {
                Start();
                var main = MainWindow();
                Require(Cards(main).Count == 0, "A new dedicated vault must have no cards.");
                if (File.Exists(VaultFile)) Require(ReadCards().Length == 0, "New vault file is not empty.");
                CheckLayout(main, "main-empty", "main.capture", "main.search", "nav.done", "nav.all");
                Screenshot(main, "01-main-empty");
            });
            if (_app is null || _app.HasExited) throw new InvalidOperationException("Owned app startup failed; dependent tests cannot run.");

            Test("layout.minimum-main-and-capture", () =>
            {
                var main = MainWindow(); var original = main.Current.BoundingRectangle;
                var transform = (TransformPattern)main.GetCurrentPattern(TransformPattern.Pattern);
                transform.Resize(900, 620);
                try
                {
                    Await(() => main.Current.BoundingRectangle.Width <= 902, "minimum window resize");
                    CheckLayout(main, "main-minimum", "main.capture", "main.search", "nav.done", "nav.all");
                    Screenshot(main, "11-main-minimum");
                    Invoke(main, "main.capture"); var capture = WindowWith("capture.title");
                    CheckLayout(capture, "capture-minimum-owner", "capture.title", "capture.next", "capture.context", "capture.save", "capture.cancel");
                    Invoke(capture, "capture.cancel"); Await(() => FindWindow("capture.title") is null, "minimum capture cancelled");
                }
                finally { transform.Resize(original.Width, original.Height); }
            });
            Test("help.explicit-open-and-close", () =>
            {
                Require(FindWindow("help.close") is null, "Help unexpectedly opened on startup.");
                Invoke(MainWindow(), "main.help"); var help = WindowWith("help.close");
                CheckLayout(help, "help", "help.capture", "help.settings", "help.close");
                Screenshot(help, "13-help"); Invoke(help, "help.close");
                Await(() => FindWindow("help.close") is null, "help closed");
            });
            string title = "Automation card " + Guid.NewGuid().ToString("N")[..8];
            const string next = "Verify the third-page revenue figures";
            const string context = "Synthetic harness context; no personal data or clipboard access.";
            string cardId = "";
            Test("capture.save-to-real-vault", () =>
            {
                Invoke(MainWindow(), "main.capture");
                var capture = WindowWith("capture.title");
                Require(capture.Current.FrameworkId == "WPF", "Capture must be a native WPF window.");
                foreach (var id in new[] { "capture.title", "capture.next", "capture.context", "capture.save", "capture.cancel" }) Element(capture, id);
                Set(capture, "capture.title", title); Set(capture, "capture.next", next); Set(capture, "capture.context", context);
                CheckLayout(capture, "capture", "capture.title", "capture.next", "capture.context", "capture.save", "capture.cancel");
                Screenshot(capture, "02-capture");
                Invoke(capture, "capture.save");
                Await(() => FindCard(title) is not null && FindWindow("capture.title") is null, "capture save and disk commit");
                var card = FindCard(title)!.Value;
                Require(card.GetProperty("nextAction").GetString() == next && card.GetProperty("context").GetString() == context, "Saved fields differ from UI values.");
                cardId = card.GetProperty("id").GetString()!;
                Element(MainWindow(), "card." + cardId);
                CheckLayout(MainWindow(), "main-detail", "main.capture", "main.search", "detail.resume", "detail.checkpoint");
                Screenshot(MainWindow(), "03-saved-card");
            });
            Test("capture.invalid-edit-retains-draft", () =>
            {
                Invoke(MainWindow(), "detail.edit"); var editor = WindowWith("capture.title");
                var before = File.ReadAllText(VaultFile);
                Set(editor, "capture.next", "   "); Invoke(editor, "capture.save");
                Await(() => Find(editor, "capture.message")?.Current.Name.Contains("下一步", StringComparison.Ordinal) == true, "invalid next-action validation");
                Require(FindWindow("capture.title") is not null, "Invalid edit closed its draft.");
                Require(Value(editor, "capture.title") == title && Value(editor, "capture.context") == context, "Validation discarded unchanged draft fields.");
                Require(File.ReadAllText(VaultFile) == before, "Rejected edit modified the saved vault.");
                Set(editor, "capture.next", next); Invoke(editor, "capture.save");
                Await(() => FindWindow("capture.title") is null, "restored valid editor save");
            });
            Test("persistence.crash-restart", () =>
            {
                Require(cardId.Length > 0, "Capture prerequisite failed.");
                Stop(_app!); Start();
                Require(FindCard(title)?.GetProperty("id").GetString() == cardId, "Card not retained after forced process termination.");
                Element(MainWindow(), "card." + cardId);
                Screenshot(MainWindow(), "04-restarted");
            });
            Test("main.search-filters", () =>
            {
                var main = MainWindow(); Set(main, "main.search", "no-match-" + Guid.NewGuid().ToString("N"));
                Await(() => Cards(main).Count == 0, "nonmatching search removes cards");
                Set(main, "main.search", title);
                Await(() => Cards(main).Count == 1, "matching search shows exactly one card");
                Set(main, "main.search", "");
                Element(main, "card." + cardId);
            });
            Test("detail.resume-and-collapse", () =>
            {
                Invoke(MainWindow(), "detail.resume");
                var resume = WindowWith("resume.collapse");
                Require(resume.Current.AutomationId == "resume.window", "Expected resume.window automation ID.");
                Require(resume.Current.ProcessId == _app!.Id, "Resume strip belongs to a different process.");
                CheckLayout(resume, "resume", "resume.expand", "resume.checkpoint", "resume.complete", "resume.collapse");
                Screenshot(resume, "05-resume-strip");
                Invoke(resume, "resume.collapse");
                Await(() => FindWindow("resume.collapse") is null, "resume strip collapse");
                Await(() => FindCard(title)?.GetProperty("status").GetString() == "active", "resume status persisted");
            });
            Test("focus.pause-resume-crash-and-pending-completion", () =>
            {
                Invoke(MainWindow(), "detail.focus");
                Await(() => ReadFocus().ValueKind == JsonValueKind.Object, "focus start committed");
                string focusId = ReadFocus().GetProperty("id").GetString()!;
                Require(ReadFocus().GetProperty("cardId").GetString() == cardId, "Focus linked to wrong card.");
                Invoke(MainWindow(), "detail.focus");
                Await(() => ReadFocus().GetProperty("paused").GetBoolean(), "focus pause committed");
                var remaining = ReadFocus().GetProperty("remainingSeconds").GetDouble();
                Stop(_app!); Start();
                Require(ReadFocus().GetProperty("id").GetString() == focusId && ReadFocus().GetProperty("paused").GetBoolean(), "Paused focus did not survive crash restart.");
                Require(ReadFocus().GetProperty("remainingSeconds").GetDouble() == remaining, "Paused remaining time changed across restart.");
                Require(Element(MainWindow(), "detail.focus").Current.Name.Contains("继续计时", StringComparison.Ordinal), "Restart UI does not expose resume timer.");
                Invoke(MainWindow(), "detail.focus");
                Await(() => !ReadFocus().GetProperty("paused").GetBoolean() && ReadFocus().GetProperty("deadline").ValueKind == JsonValueKind.String, "focus resume committed");
                Stop(_app!);
                // Synthetic expired pending fixture ONLY in our vault, while its owner is stopped.
                var now = DateTimeOffset.UtcNow;
                var fixture = JsonSerializer.Serialize(new { id = focusId, cardId, minutes = 1, startedAt = now.AddMinutes(-2), deadline = now.AddMinutes(-1), remainingSeconds = 0, paused = false, completionPending = true }, Json);
                File.WriteAllText(Path.Combine(_vault, "focus.json"), fixture);
                Start();
                string marker = "[focus:" + focusId + "]";
                Await(() => FindCard(title)?.GetProperty("checkpoints").EnumerateArray().Count(c => c.GetProperty("text").GetString()!.Contains(marker, StringComparison.Ordinal)) == 1 && ReadFocus().ValueKind == JsonValueKind.Null, "pending focus completion checkpoint and acknowledgement");
                Stop(_app!);
                // Replay the same pending fixture: must not double-record a checkpoint.
                File.WriteAllText(Path.Combine(_vault, "focus.json"), fixture); Start();
                Await(() => ReadFocus().ValueKind == JsonValueKind.Null, "replayed completion acknowledged");
                Require(FindCard(title)?.GetProperty("checkpoints").EnumerateArray().Count(c => c.GetProperty("text").GetString()!.Contains(marker, StringComparison.Ordinal)) == 1, "Pending completion was duplicated.");
            });
            var editedTitle = title + " edited";
            Test("detail.edit-persists", () =>
            {
                Invoke(MainWindow(), "detail.edit"); var capture = WindowWith("capture.title");
                Require(Value(capture, "capture.title") == title, "Editor did not load existing title.");
                Set(capture, "capture.title", editedTitle); Set(capture, "capture.next", next + " (edited)");
                Invoke(capture, "capture.save");
                Await(() => FindCard(editedTitle)?.GetProperty("nextAction").GetString() == next + " (edited)", "edited disk fields");
                Require(FindCard(editedTitle)?.GetProperty("id").GetString() == cardId, "Edit replaced the card identity.");
            });
            Test("detail.checkpoint-persists", () =>
            {
                Invoke(MainWindow(), "detail.resume"); var strip = WindowWith("resume.collapse");
                Invoke(MainWindow(), "detail.checkpoint"); var prompt = WindowWith("prompt.input");
                Set(prompt, "prompt.input", "Harness checkpoint: figures reconciled.");
                Set(prompt, "prompt.next", "Advance to the fourth-page review");
                CheckLayout(prompt, "checkpoint-prompt", "prompt.input", "prompt.next", "prompt.confirm"); Screenshot(prompt, "06-checkpoint");
                Invoke(prompt, "prompt.confirm");
                Await(() => FindCard(editedTitle)?.GetProperty("checkpoints").EnumerateArray().Any(c => c.GetProperty("text").GetString() == "Harness checkpoint: figures reconciled.") == true, "checkpoint written to vault");
                Await(() => FindCard(editedTitle)?.GetProperty("nextAction").GetString() == "Advance to the fourth-page review", "progress advanced next action on disk");
                Await(() => Element(strip, "resume.next").Current.Name == "Advance to the fourth-page review", "live strip next action update");
                Invoke(strip, "resume.collapse");
            });
            Test("detail.complete-and-navigation", () =>
            {
                Invoke(MainWindow(), "detail.complete");
                Await(() => FindCard(editedTitle)?.GetProperty("status").GetString() == "done", "done status written to vault");
                Require(FindCard(editedTitle)?.GetProperty("completedAt").ValueKind == JsonValueKind.String, "Completion timestamp missing.");
                Invoke(MainWindow(), "nav.done"); Element(MainWindow(), "card." + cardId);
                Screenshot(MainWindow(), "07-done");
                Invoke(MainWindow(), "nav.all"); Element(MainWindow(), "card." + cardId);
            });
            Test("capture.ctrl-enter", () =>
            {
                Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
                Set(capture, "capture.title", "Shortcut card"); Set(capture, "capture.next", "Saved by actual Ctrl+Enter input");
                Focus(capture, "capture.next"); SendChord(0x11, 0x0D);
                Await(() => FindCard("Shortcut card") is not null && FindWindow("capture.title") is null, "Ctrl+Enter save");
            });
            Test("single-instance.resource-forwarding", () =>
            {
                var resource = Path.Combine(_vault, "owned-resource.txt"); File.WriteAllText(resource, "Synthetic resource; never automatically opened.");
                var secondary = Launch("--capture", "--resource", resource);
                Require(secondary.WaitForExit(WaitMs), "Secondary process did not exit after forwarding.");
                Require(secondary.ExitCode == 0, "Secondary process exited with error.");
                var capture = WindowWith("capture.title");
                Require(capture.Current.ProcessId == _app!.Id, "Forwarded capture is not in the primary PID.");
                Set(capture, "capture.title", "Forwarded resource card"); Set(capture, "capture.next", "Review synthetic local resource");
                var secondResource = Path.Combine(_vault, "owned-resource-two.txt"); File.WriteAllText(secondResource, "Second synthetic resource.");
                var repeated = Launch("--capture", "--resource", secondResource);
                Require(repeated.WaitForExit(WaitMs) && repeated.ExitCode == 0, "Repeated resource forwarding did not exit.");
                Require(Value(capture, "capture.title") == "Forwarded resource card", "Repeated forwarding lost an open draft.");
                Invoke(capture, "capture.save");
                Await(() => FindCard("Forwarded resource card")?.GetProperty("resources").EnumerateArray().Any(r => r.GetProperty("target").GetString() == resource) == true, "forwarded resource persisted");
                Require(FindCard("Forwarded resource card")?.GetProperty("resources").EnumerateArray().Any(r => r.GetProperty("target").GetString() == secondResource) == true, "Second forwarded resource not attached to existing draft.");
                Invoke(MainWindow(), "detail.resources"); var resources = WindowWith("resources.openSelected");
                Require(!Element(resources, "resources.openSelected").Current.IsEnabled, "Resource opener should initially require selection.");
                Invoke(resources, "resources.cancel");
            });
            Test("resume.checkpoint-and-complete-with-hidden-dashboard", () =>
            {
                Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
                const string stripTitle = "Strip-only workflow card";
                Set(capture, "capture.title", stripTitle); Set(capture, "capture.next", "Original strip next action");
                Invoke(capture, "capture.save"); Await(() => FindCard(stripTitle) is not null && FindWindow("capture.title") is null, "strip fixture persisted");
                var main = MainWindow(); Invoke(main, "detail.resume"); var strip = WindowWith("resume.collapse");
                var mainPattern = (WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern);
                mainPattern.SetWindowVisualState(WindowVisualState.Minimized);
                var mainHwnd = new IntPtr(main.Current.NativeWindowHandle);
                // WPF UIA can report IsOffscreen=false for a minimized top-level HWND.
                // Native IsIconic and WindowPattern state are authoritative for minimization.
                void DashboardRemainsHidden() => Require(Native.IsIconic(mainHwnd) && mainPattern.Current.WindowVisualState == WindowVisualState.Minimized, "Strip action unexpectedly exposed the dashboard.");
                Await(() => Native.IsIconic(mainHwnd) && mainPattern.Current.WindowVisualState == WindowVisualState.Minimized, "dashboard minimized for strip-only workflow");
                Invoke(strip, "resume.checkpoint"); var progress = WindowWith("prompt.input");
                DashboardRemainsHidden();
                Set(progress, "prompt.input", "Strip-only checkpoint committed");
                Set(progress, "prompt.next", "Updated from the strip without dashboard");
                Invoke(progress, "prompt.confirm");
                Await(() => FindWindow("prompt.input") is null && FindCard(stripTitle)?.GetProperty("nextAction").GetString() == "Updated from the strip without dashboard", "strip progress persisted");
                Await(() => Element(strip, "resume.next").Current.Name == "Updated from the strip without dashboard", "strip text refreshed without dashboard");
                Require(FindCard(stripTitle)?.GetProperty("checkpoints").EnumerateArray().Any(c => c.GetProperty("text").GetString() == "Strip-only checkpoint committed") == true, "Strip checkpoint missing on disk.");
                DashboardRemainsHidden(); Screenshot(strip, "14-strip-only-progress");
                Invoke(strip, "resume.complete");
                Await(() => FindCard(stripTitle)?.GetProperty("status").GetString() == "done" && FindWindow("resume.collapse") is null, "strip-only completion persisted");
                DashboardRemainsHidden(); mainPattern.SetWindowVisualState(WindowVisualState.Normal);
            });
            Test("global-hotkey.ctrl-alt-space", () =>
            {
                if (!_hotkeyAvailable) throw new SkipException("Ctrl+Alt+Space was occupied before this run; no input sent to another application's hotkey.");
                // Confirm our app claims the binding before synthetic global input.
                string status = IntegrationStatus();
                Require(status.Contains("Ctrl+Alt+Space 已启用", StringComparison.Ordinal), "Owned app does not report the hotkey registered: " + status);
                var main = MainWindow();
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Minimized);
                SendChord(0x11, 0x12, 0x20);
                var capture = WindowWith("capture.title");
                Screenshot(capture, "08-global-hotkey"); Invoke(capture, "capture.cancel");
                Await(() => FindWindow("capture.title") is null, "hotkey capture cancelled");
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            });
            Test("hotkey.registered-window-handler", () =>
            {
                // Distinct from the real SendInput test: verifies only registered HWND dispatch/UI path.
                var main = MainWindow(); EnsureOwned(main); var hwnd = new IntPtr(main.Current.NativeWindowHandle);
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Minimized);
                Require(Native.PostMessage(hwnd, 0x0312, new IntPtr(0x5241), new IntPtr((0x20 << 16) | 3)), "Posting to owned hotkey HWND failed.");
                var capture = WindowWith("capture.title");
                Require(((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState == WindowVisualState.Minimized, "Capture handler restored the dashboard unexpectedly.");
                Screenshot(capture, "12-hotkey-handler"); Invoke(capture, "capture.cancel");
                Await(() => FindWindow("capture.title") is null, "handler capture cancelled");
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            });
            if (_stressRestarts > 0) Test("persistence.repeated-owned-crash-restarts", () =>
            {
                var expected = File.ReadAllBytes(VaultFile); var sw = Stopwatch.StartNew();
                for (int trial = 1; trial <= _stressRestarts; trial++)
                {
                    Stop(_app!); Start(); Invoke(MainWindow(), "nav.all");
                    Element(MainWindow(), "card." + cardId);
                    Require(File.ReadAllBytes(VaultFile).SequenceEqual(expected), "Vault bytes changed during restart trial " + trial);
                    Require(FindCard(editedTitle)?.GetProperty("status").GetString() == "done", "Completed card changed at trial " + trial);
                    Console.Error.WriteLine($"[stress] owned crash restart {trial}/{_stressRestarts}");
                }
                Metrics.Add(new { name = "restartStress", completedTrials = _stressRestarts, elapsedMs = sw.ElapsedMilliseconds });
            });
            Test("capture.cold-and-warm-hidden-dashboard", () =>
            {
                Stop(_app!); _app = Launch("--capture");
                var capture = WindowWith("capture.title");
                AssertDashboardHidden(); Screenshot(capture, "09-cold-capture");
                Invoke(capture, "capture.cancel"); Await(() => FindWindow("capture.title") is null, "cold capture cancelled");
                var secondary = Launch("--capture"); Require(secondary.WaitForExit(WaitMs) && secondary.ExitCode == 0, "Warm capture request did not forward.");
                capture = WindowWith("capture.title"); AssertDashboardHidden(); Screenshot(capture, "10-warm-hidden-capture");
                Invoke(capture, "capture.cancel"); Await(() => FindWindow("capture.title") is null, "warm capture cancelled");
            });
            Test("resume.warm-secondary-keeps-dashboard-minimized", () => InScenarioVault("resume-warm", () =>
            {
                const string fixtureTitle = "Dedicated warm resume card";
                CreateScenarioCard(fixtureTitle, "Resume without restoring the dashboard");
                var main = MainWindow(); EnsureOwned(main);
                var state = (WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern);
                var hwnd = new IntPtr(main.Current.NativeWindowHandle);
                state.SetWindowVisualState(WindowVisualState.Minimized);
                Await(() => Native.IsIconic(hwnd) && state.Current.WindowVisualState == WindowVisualState.Minimized, "warm resume dashboard minimized");
                Forward("--resume");
                var strip = WindowWith("resume.collapse");
                RequireVisible(strip, "warm resume strip");
                Require(Element(strip, "resume.next").Current.Name == "Resume without restoring the dashboard", "Warm resume selected the wrong card.");
                Await(() => FindCard(fixtureTitle)?.GetProperty("status").GetString() == "active", "warm resume persisted active status");
                Require(Native.IsIconic(hwnd) && state.Current.WindowVisualState == WindowVisualState.Minimized, "Secondary --resume restored the minimized dashboard.");
                Require(FindWindow("capture.title") is null, "Eligible warm resume unexpectedly opened capture.");
                Screenshot(strip, "15-warm-quiet-resume");
                Invoke(strip, "resume.collapse");
                Await(() => FindWindow("resume.collapse") is null, "warm resume collapsed");
                Require(Native.IsIconic(hwnd) && state.Current.WindowVisualState == WindowVisualState.Minimized, "Collapsing warm resume exposed the dashboard.");
            }));
            Test("resume.cold-eligible-keeps-dashboard-hidden", () => InScenarioVault("resume-cold", () =>
            {
                const string fixtureTitle = "Dedicated cold resume card";
                CreateScenarioCard(fixtureTitle, "Cold resume directly into the strip");
                Stop(_app!); _app = Launch("--resume");
                var strip = WindowWith("resume.collapse");
                RequireVisible(strip, "cold resume strip"); AssertDashboardHidden();
                Require(Element(strip, "resume.next").Current.Name == "Cold resume directly into the strip", "Cold resume did not load the persisted eligible card.");
                Await(() => FindCard(fixtureTitle)?.GetProperty("status").GetString() == "active", "cold resume persisted active status");
                Require(FindWindow("capture.title") is null, "Eligible cold resume unexpectedly opened capture.");
                Screenshot(strip, "16-cold-quiet-resume");
                Invoke(strip, "resume.collapse");
                Await(() => FindWindow("resume.collapse") is null, "cold resume collapsed"); AssertDashboardHidden();
            }));
            Test("resume.cold-no-eligible-opens-capture-only", () => InScenarioVault("resume-no-eligible", () =>
            {
                const string fixtureTitle = "Dedicated completed resume card";
                CreateScenarioCard(fixtureTitle, "This completed card must not resume");
                Invoke(MainWindow(), "detail.complete");
                Await(() => FindCard(fixtureTitle)?.GetProperty("status").GetString() == "done", "no-eligible fixture completed");
                var before = File.ReadAllText(VaultFile);
                Stop(_app!); _app = Launch("--resume");
                var capture = WindowWith("capture.title"); RequireVisible(capture, "no-eligible capture");
                AssertDashboardHidden();
                Require(FindWindow("resume.collapse") is null, "No-eligible resume incorrectly opened a strip.");
                Require(Value(capture, "capture.title") == "" && Value(capture, "capture.next") == "", "No-eligible resume did not open a fresh capture.");
                Require(File.ReadAllText(VaultFile) == before, "No-eligible resume modified completed cards.");
                Screenshot(capture, "17-no-eligible-capture");
                Invoke(capture, "capture.cancel");
                Await(() => FindWindow("capture.title") is null, "no-eligible capture cancelled"); AssertDashboardHidden();
            }));
            Test("progress.blank-note-updates-next-without-checkpoint", () => InScenarioVault("progress-next-only", () =>
            {
                const string fixtureTitle = "Dedicated next-only progress card";
                CreateScenarioCard(fixtureTitle, "Original next-only action");
                SeedScenarioCheckpoint("Existing progress must survive");
                var before = FindCard(fixtureTitle)!.Value;
                Invoke(MainWindow(), "detail.resume"); var strip = WindowWith("resume.collapse");
                Invoke(strip, "resume.checkpoint"); var prompt = WindowWith("prompt.input");
                Set(prompt, "prompt.input", ""); Set(prompt, "prompt.next", "Updated next action without a progress note");
                Invoke(prompt, "prompt.confirm");
                Await(() => FindWindow("prompt.input") is null && FindCard(fixtureTitle)?.GetProperty("nextAction").GetString() == "Updated next action without a progress note", "blank-progress next action saved");
                AssertCheckpointAndContextUnchanged(before, FindCard(fixtureTitle)!.Value);
                Await(() => Element(strip, "resume.next").Current.Name == "Updated next action without a progress note", "next-only live strip refresh");
                Screenshot(strip, "18-next-only-progress");
            }));
            Test("progress.blank-note-identical-next-stays-inline", () => InScenarioVault("progress-no-op", () =>
            {
                const string fixtureTitle = "Dedicated no-op progress card";
                const string fixtureNext = "Unchanged next action";
                CreateScenarioCard(fixtureTitle, fixtureNext); SeedScenarioCheckpoint("Retain prior checkpoint through validation");
                var before = FindCard(fixtureTitle)!.Value; var bytes = File.ReadAllText(VaultFile);
                Invoke(MainWindow(), "detail.checkpoint"); var prompt = WindowWith("prompt.input");
                Set(prompt, "prompt.input", ""); Set(prompt, "prompt.next", fixtureNext); Invoke(prompt, "prompt.confirm");
                Await(() => Find(prompt, "prompt.error") is { } error && !error.Current.IsOffscreen && !string.IsNullOrWhiteSpace(error.Current.Name), "unchanged blank-progress inline validation");
                Require(FindWindow("prompt.input") is not null, "No-op progress falsely closed the dialog.");
                Require(Value(prompt, "prompt.input") == "" && Value(prompt, "prompt.next") == fixtureNext, "No-op validation discarded dialog fields.");
                Require(File.ReadAllText(VaultFile) == bytes, "No-op progress modified the saved vault.");
                Screenshot(prompt, "19-no-op-progress-validation");
                Set(prompt, "prompt.next", "Changed next after inline validation"); Invoke(prompt, "prompt.confirm");
                Await(() => FindWindow("prompt.input") is null && FindCard(fixtureTitle)?.GetProperty("nextAction").GetString() == "Changed next after inline validation", "same dialog saves changed next with blank progress");
                AssertCheckpointAndContextUnchanged(before, FindCard(fixtureTitle)!.Value);
            }));
            Test("draft.capture-crash-explicit-recovery-and-save", () => InScenarioVault("draft-crash", () =>
            {
                const string draftTitle = "Recover this synthetic unsaved capture";
                const string draftNext = "Continue after explicit recovery";
                const string draftContext = "Unsaved context must survive a forced owned-process stop.";
                Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
                Require(((TogglePattern)Element(capture, "capture.autoDraft").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On, "New capture autosave must default on.");
                Set(capture, "capture.title", draftTitle); Set(capture, "capture.next", draftNext); Set(capture, "capture.context", draftContext);
                Await(() => DraftCard(draftTitle) is { } draft && draft.GetProperty("nextAction").GetString() == draftNext && draft.GetProperty("context").GetString() == draftContext, "capture draft flushed to dedicated vault");
                Await(() => Element(capture, "capture.draftStatus").Current.Name.Contains("已暂存", StringComparison.Ordinal), "draft autosave status");
                string draftId = DraftCard(draftTitle)!.Value.GetProperty("id").GetString()!;
                Require(ReadCards().Length == 0, "Autosave incorrectly created a formal card.");
                Screenshot(capture, "20-autosaved-capture-draft");
                Stop(_app!); Start();
                Require(FindWindow("capture.title") is null && FindWindow("drafts.close") is null, "Startup unsolicitedly opened draft recovery.");
                Require(ReadCards().Length == 0, "Crash restart imported a draft without consent.");
                var recovered = RestoreDraft(draftId);
                Require(Value(recovered, "capture.title") == draftTitle && Value(recovered, "capture.next") == draftNext && Value(recovered, "capture.context") == draftContext, "Explicit draft recovery lost fields.");
                Screenshot(recovered, "21-recovered-capture-draft");
                Invoke(recovered, "capture.save");
                Await(() => FindWindow("capture.title") is null && FindCard(draftTitle)?.GetProperty("id").GetString() == draftId && DraftFiles().Length == 0, "recovered card formally saved with same identity and draft removed");
                Require(ReadCards().Length == 1, "Recovered draft saved duplicate cards.");
                Stop(_app!); Start();
                Require(ReadCards().Length == 1 && FindCard(draftTitle)?.GetProperty("id").GetString() == draftId && DraftFiles().Length == 0, "Recovered formal card or draft cleanup changed after restart.");
                Require(Find(MainWindow(), "main.drafts") is not { } entry || entry.Current.IsOffscreen, "Completed recovery still advertises a recoverable draft.");
            }));
            Test("draft.capture-opt-out-keeps-typing-off-disk", () => InScenarioVault("draft-opt-out", () =>
            {
                const string draftTitle = "Synthetic draft with autosave disabled";
                Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
                var autoDraft = Element(capture, "capture.autoDraft"); EnsureOwned(autoDraft);
                var toggle = (TogglePattern)autoDraft.GetCurrentPattern(TogglePattern.Pattern);
                Require(toggle.Current.ToggleState == ToggleState.On, "New capture autosave must default on.");
                toggle.Toggle(); Await(() => toggle.Current.ToggleState == ToggleState.Off, "capture autosave opted out");
                Set(capture, "capture.title", draftTitle); Set(capture, "capture.next", "Save only when explicitly requested");
                Set(capture, "capture.context", "No draft file should contain these synthetic fields.");
                // Observe beyond the 800 ms debounce, rather than checking before a timer could fire.
                var observation = Stopwatch.StartNew();
                while (observation.ElapsedMilliseconds < 1800)
                {
                    Require(DraftFiles().Length == 0 && ReadCards().Length == 0, "Opted-out capture wrote draft or formal data before explicit save.");
                    Thread.Sleep(80);
                }
                Require(DraftFiles().Length == 0, "Opted-out capture wrote a delayed draft.");
                Metrics.Add(new { name = "draftOptOutObservation", elapsedMs = observation.ElapsedMilliseconds, draftFiles = 0 });
                Invoke(capture, "capture.save");
                Await(() => FindWindow("capture.title") is null && FindCard(draftTitle) is not null, "opted-out capture explicitly saved");
                Require(DraftFiles().Length == 0, "Explicit save after opt-out left a draft file.");
            }));
            Test("draft.keep-close-remains-uncommitted-until-recovery", () => InScenarioVault("draft-keep", () =>
            {
                const string draftTitle = "Keep this capture for later";
                Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
                Set(capture, "capture.title", draftTitle); Set(capture, "capture.next", "Resume editing the kept draft");
                Invoke(capture, "capture.keepDraft");
                Await(() => FindWindow("capture.title") is null && DraftCard(draftTitle) is not null, "keep draft saved and closed capture");
                Require(ReadCards().Length == 0, "Keep draft incorrectly committed a formal card.");
                string draftId = DraftCard(draftTitle)!.Value.GetProperty("id").GetString()!;
                var recovered = RestoreDraft(draftId);
                Require(Value(recovered, "capture.title") == draftTitle && Value(recovered, "capture.next") == "Resume editing the kept draft", "Kept draft lost captured fields.");
                Set(recovered, "capture.next", "Finish editing after keep-draft recovery"); Invoke(recovered, "capture.save");
                Await(() => FindWindow("capture.title") is null && FindCard(draftTitle)?.GetProperty("nextAction").GetString() == "Finish editing after keep-draft recovery" && DraftFiles().Length == 0, "kept draft formally saved and removed");
                Require(FindCard(draftTitle)?.GetProperty("id").GetString() == draftId && ReadCards().Length == 1, "Keep-draft recovery changed identity or duplicated the card.");
            }));
            Test("hotkey.collision-status", () =>
            {
                Stop(_app!);
                bool held = Native.RegisterHotKey(IntPtr.Zero, HotkeyId, 0x4003, 0x20);
                int error = Marshal.GetLastWin32Error();
                try
                {
                    Start();
                    string status = IntegrationStatus();
                    Require(status.Contains("Ctrl+Alt+Space", StringComparison.Ordinal) && (status.Contains("注册失败", StringComparison.Ordinal) || status.Contains("未注册", StringComparison.Ordinal)) && status.Contains("1409", StringComparison.Ordinal), "Collision must explicitly report registration error 1409: " + status);
                    Metrics.Add(new { name = "hotkeyConflict", ownedReservation = held, reservationError = held ? 0 : error, status });
                }
                finally { if (held) Native.UnregisterHotKey(IntPtr.Zero, HotkeyId); }
            });
            if (Results.Any(r => r.name == "global-hotkey.ctrl-alt-space" && r.status == "failed")) Test("hotkey.independent-delivery-probe", () =>
            {
                Stop(_app!);
                using var helper = new HwndSource(new HwndSourceParameters("Relay.Automation owned hotkey probe") { Width = 1, Height = 1, WindowStyle = 0 });
                bool received = false;
                helper.AddHook((IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
                {
                    if (message == 0x0312 && wparam.ToInt32() == HotkeyId) { received = true; handled = true; }
                    return IntPtr.Zero;
                });
                if (!Native.RegisterHotKey(helper.Handle, HotkeyId, 0x4003, 0x20)) throw new SkipException("Independent probe cannot reserve binding; error " + Marshal.GetLastWin32Error());
                try
                {
                    SendChord(0x11, 0x12, 0x20);
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
                    timeout.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timeout.Stop();
                    _independentDeliveryMissing = !received;
                    Metrics.Add(new { name = "independentHotkeyDelivery", received, ownedWindow = helper.Handle.ToInt64(), observationMs = 2000 });
                    Require(received, "SendInput returned success but an independent owned registered HWND also received no WM_HOTKEY; environment delivery is unverified.");
                }
                finally { Native.UnregisterHotKey(helper.Handle, HotkeyId); }
            });
        }
        catch (Exception ex) { Record("harness.fatal", "failed", 0, ex.ToString()); }
        finally
        {
            foreach (var process in Owned) { try { Stop(process); } catch (Exception ex) { Record("cleanup." + process.Id, "failed", 0, ex.Message); } }
            if (_output.Length > 0)
            {
                var report = new { schemaVersion = 1, finishedUtc = DateTime.UtcNow, executable = _exe, vault = _vault, elapsedMs = Session.ElapsedMilliseconds,
                    failures = _failures, productFunctionalFailures = _failures - EnvironmentFailures, environmentFailures = EnvironmentFailures,
                    results = Results,
                    classifiedResults = Results.Select(r => new { r.name, status = EnvironmentCorroborated && r.status == "failed" && r.name is "global-hotkey.ctrl-alt-space" or "hotkey.independent-delivery-probe" ? "blocked" : r.status, rawStatus = r.status, r.durationMs, r.detail }),
                    environmentDiagnostics = new { independentRegisteredHelperReceivedNoHotkey = _independentDeliveryMissing, reason = EnvironmentCorroborated ? "SendInput accepted the same balanced sequence but neither independent owned helper nor app received the expected global hotkey. Physical keyboard delivery remains unverified; raw failed assertions retained." : null },
                    metrics = Metrics, layouts = Layouts, screenshots = Screenshots,
                    safety = new { ownedProcessIds = Owned.Select(p => p.Id).ToArray(), clipboardRead = false, registryModified = false, screenshotScope = "Owned WPF HWND PrintWindow plus opt-in app-owned visual tree RenderTargetBitmap; no desktop capture", vaultRetained = true } };
                var text = JsonSerializer.Serialize(report, Json); File.WriteAllText(Path.Combine(_output, "report.json"), text); Console.WriteLine(text);
                Console.Error.WriteLine("Report: " + Path.Combine(_output, "report.json"));
            }
        }
        return _failures - EnvironmentFailures > 0 ? 1 : EnvironmentFailures > 0 ? 2 : 0;
    }

    private static string Workspace(string[] args)
    {
        string? workspace = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing option value.");
            if (args[i] == "--workspace") workspace = Path.GetFullPath(args[i + 1]);
            else if (args[i] == "--configuration" && args[i+1] == "release") _release=true;
            else if (args[i] == "--configuration" && args[i+1] == "debug") _release=false;
            else if (args[i] == "--stress-restarts" && int.TryParse(args[i + 1], out int count) && count is >= 0 and <= 100) _stressRestarts = count;
            else throw new ArgumentException("Usage: Relay.Automation [--workspace <repository-root>] [--stress-restarts <0..100>] [--configuration <debug|release>]");
        }
        if (workspace is not null) return workspace;
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "native", "Relay.Desktop"))) return d.FullName;
        throw new DirectoryNotFoundException("Run from the repository or pass --workspace.");
    }
    // Every 2.1 scenario owns a fresh nested vault; original suite selection/state is restored even on failure.
    private static void InScenarioVault(string name, Action body)
    {
        string originalVault = _vault;
        if (_app is not null) Stop(_app);
        _vault = Path.Combine(originalVault, "scenario-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vault);
        Metrics.Add(new { name = "scenarioVault", scenario = name, vault = _vault });
        try { Start(); body(); }
        catch
        {
            try
            {
                var window = FindWindow("prompt.input") ?? FindWindow("capture.title") ?? FindWindow("resume.collapse") ?? FindWindow("main.capture");
                if (window is not null) Screenshot(window, "failure-" + name);
            }
            catch { }
            throw;
        }
        finally
        {
            try { if (_app is not null) Stop(_app); }
            finally { _vault = originalVault; }
            Start();
        }
    }
    private static void CreateScenarioCard(string title, string next)
    {
        Require(ReadCards().Length == 0, "Scenario vault must begin empty.");
        Invoke(MainWindow(), "main.capture"); var capture = WindowWith("capture.title");
        Set(capture, "capture.title", title); Set(capture, "capture.next", next);
        Set(capture, "capture.context", "Dedicated synthetic 2.1 fixture; preserve this context.");
        Invoke(capture, "capture.save");
        Await(() => FindCard(title) is not null && FindWindow("capture.title") is null, "dedicated scenario card committed");
        Require(ReadCards().Length == 1, "Scenario must not inherit cards from another test.");
        Invoke(MainWindow(), "card." + FindCard(title)!.Value.GetProperty("id").GetString());
    }
    private static void SeedScenarioCheckpoint(string text)
    {
        Invoke(MainWindow(), "detail.checkpoint"); var prompt = WindowWith("prompt.input");
        Set(prompt, "prompt.input", text); Invoke(prompt, "prompt.confirm");
        Await(() => FindWindow("prompt.input") is null && ReadCards()[0].GetProperty("checkpoints").GetArrayLength() == 1, "dedicated existing checkpoint committed");
    }
    private static void AssertCheckpointAndContextUnchanged(JsonElement before, JsonElement after)
    {
        Require(after.GetProperty("checkpoints").GetArrayLength() == before.GetProperty("checkpoints").GetArrayLength(), "Blank progress created an empty checkpoint.");
        foreach (string field in new[] { "id", "context", "resources", "checkpoints" })
            Require(after.GetProperty(field).GetRawText() == before.GetProperty(field).GetRawText(), "Next-only progress unexpectedly changed " + field + ".");
    }
    private static string[] DraftFiles()
    {
        string directory = Path.Combine(_vault, "drafts");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly) : [];
    }
    private static JsonElement? DraftCard(string title)
    {
        foreach (string path in DraftFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var envelope = document.RootElement;
            Require(envelope.GetProperty("version").GetInt32() == 1, "Unexpected draft schema version.");
            Require(envelope.GetProperty("savedAt").ValueKind == JsonValueKind.String, "Draft save timestamp missing.");
            var card = envelope.GetProperty("card");
            if (card.GetProperty("title").GetString() == title) return card.Clone();
        }
        return null;
    }
    private static AutomationElement RestoreDraft(string id)
    {
        Await(() => Find(MainWindow(), "main.drafts") is { } entry && !entry.Current.IsOffscreen, "explicit draft recovery entry");
        Invoke(MainWindow(), "main.drafts"); var recovery = WindowWith("drafts.close");
        Invoke(recovery, "drafts.restore." + id);
        return WindowWith("capture.title");
    }
    private static void Forward(params string[] args)
    {
        var secondary = Launch(args);
        Require(secondary.WaitForExit(WaitMs) && secondary.ExitCode == 0, "Secondary request did not exit successfully after forwarding.");
    }
    private static void RequireVisible(AutomationElement window, string description)
    {
        EnsureOwned(window);
        Require(!window.Current.IsOffscreen && !window.Current.BoundingRectangle.IsEmpty && window.Current.BoundingRectangle.Width > 0, description + " is not visible.");
    }
    private static string VaultFile => Path.Combine(_vault, "vault.json");
    private static JsonElement ReadFocus()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_vault, "focus.json")));
        return doc.RootElement.Clone();
    }
    private static JsonElement[] ReadCards()
    {
        if (!File.Exists(VaultFile)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(VaultFile));
        return doc.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.Clone()).ToArray();
    }
    private static JsonElement? FindCard(string title) => ReadCards().Where(c => c.GetProperty("title").GetString() == title).Select(c => (JsonElement?)c).FirstOrDefault();
    private static Process Launch(params string[] extra)
    {
        var info = new ProcessStartInfo(_exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exe)! };
        info.ArgumentList.Add("--vault"); info.ArgumentList.Add(_vault);
        var diagnostics = Path.Combine(_output, "app-visuals", Guid.NewGuid().ToString("N"));
        info.ArgumentList.Add("--diagnostic-snapshots"); info.ArgumentList.Add(diagnostics);
        foreach (var arg in extra) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null."); Owned.Add(process); DiagnosticDirectories[process.Id] = diagnostics; return process;
    }
    private static void Start()
    {
        var sw = Stopwatch.StartNew(); _app = Launch();
        var main = MainWindow(); Element(main, "main.capture");
        _app.Refresh(); Metrics.Add(new { name = "startup", processId = _app.Id, startupMs = sw.ElapsedMilliseconds, workingSetBytes = _app.WorkingSet64 });
    }
    private static void Stop(Process process)
    {
        if (!Owned.Contains(process)) throw new InvalidOperationException("Refusing to terminate an unowned process.");
        if (!process.HasExited) { process.Kill(entireProcessTree: false); if (!process.WaitForExit(5000)) throw new TimeoutException("Owned process did not terminate."); }
    }
    private static void AssertDashboardHidden()
    {
        var main = FindWindow("main.capture");
        Require(main is null || main.Current.IsOffscreen, "Background entry exposed the dashboard.");
        Metrics.Add(new { name = "backgroundCapture", processId = _app!.Id, dashboardExposed = false });
    }
    private static AutomationElement MainWindow() => WindowWith("main.capture");
    private static AutomationElement WindowWith(string id)
    {
        AutomationElement? found = null; Await(() => (found = FindWindow(id)) is not null, "owned window containing " + id); return found!;
    }
    private static AutomationElement? FindWindow(string id)
    {
        if (_app is null || _app.HasExited) return null;
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _app.Id));
        foreach (AutomationElement window in windows)
        {
            var match = window.Current.AutomationId == id ? window : Find(window, id);
            // Owned modal WPF windows can be descendants of the owner in the UIA tree.
            // Return the closest native window, not the outer dashboard HWND.
            while (match is not null && match.Current.ProcessId == _app.Id)
            {
                if (match.Current.ControlType == ControlType.Window && match.Current.NativeWindowHandle != 0) return match;
                match = TreeWalker.ControlViewWalker.GetParent(match);
            }
        }
        return null;
    }
    private static AutomationElement? Find(AutomationElement parent, string id) => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static AutomationElement Element(AutomationElement parent, string id)
    {
        AutomationElement? result = null; Await(() => (result = Find(parent, id)) is not null, "control " + id); return result!;
    }
    private static List<AutomationElement> Cards(AutomationElement window) => window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
        .Cast<AutomationElement>().Where(e => e.Current.AutomationId.StartsWith("card.", StringComparison.Ordinal)).ToList();
    private static void Invoke(AutomationElement parent, string id)
    {
        var element = Element(parent, id); EnsureOwned(element);
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
        Require(element.Current.IsEnabled, id + " is disabled.");
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static void Set(AutomationElement parent, string id, string value)
    {
        var element = Element(parent, id); EnsureOwned(element); ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    }
    private static string Value(AutomationElement parent, string id) => ((ValuePattern)Element(parent, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static void EnsureOwned(AutomationElement element) => Require(_app is not null && element.Current.ProcessId == _app.Id && !_app.HasExited, "Refusing UI action outside owned PID.");
    private static void Await(Func<bool> condition, string description, int timeout = WaitMs)
    {
        var sw = Stopwatch.StartNew(); Exception? last = null;
        while (sw.ElapsedMilliseconds < timeout && Session.Elapsed < TimeSpan.FromMinutes(5 + _stressRestarts / 10.0))
        {
            try { if (condition()) return; }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or IOException or JsonException or COMException) { last = ex; }
            Thread.Sleep(80); // Bounded UI/file readiness wait, not tool/job polling.
        }
        throw new TimeoutException("Timed out waiting for " + description + (last is null ? "" : ": " + last.Message));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Test(string name, Action body)
    {
        var sw = Stopwatch.StartNew();
        try { body(); Record(name, "passed", sw.ElapsedMilliseconds, null); }
        catch (SkipException ex)
        {
            Record(name, "skipped", sw.ElapsedMilliseconds, ex.Message);
            try { if (_app is not null && !_app.HasExited) { Stop(_app); Start(); Invoke(MainWindow(), "nav.all"); } }
            catch (Exception recovery) { Console.Error.WriteLine("Recovery failed: " + recovery.Message); }
        }
        catch (Exception ex)
        {
            Record(name, "failed", sw.ElapsedMilliseconds, ex.ToString());
            try { var window = FindWindow("capture.title") ?? FindWindow("prompt.input") ?? FindWindow("main.capture"); if (window is not null) Screenshot(window, "failure-" + name); } catch { }
            // Do not leave failed modal dialogs blocking all later independent scenarios.
            try { if (_app is not null && !_app.HasExited) { Stop(_app); Start(); Invoke(MainWindow(), "nav.all"); } }
            catch (Exception recovery) { Console.Error.WriteLine("Recovery failed: " + recovery.Message); }
        }
    }
    private static void Record(string name, string status, long durationMs, string? detail)
    {
        Results.Add(new TestResult(name, status, durationMs, detail)); if (status == "failed") _failures++;
        Console.Error.WriteLine($"[{status}] {name} ({durationMs} ms){(detail is null ? "" : ": " + detail)}");
    }
    private static object Rectangle(Rect r) => new { x = r.IsEmpty ? 0 : r.X, y = r.IsEmpty ? 0 : r.Y, width = r.IsEmpty ? 0 : r.Width, height = r.IsEmpty ? 0 : r.Height };
    private static void CheckLayout(AutomationElement window, string surface, params string[] ids)
    {
        EnsureOwned(window); var bounds = window.Current.BoundingRectangle; var errors = new List<string>();
        foreach (var id in ids)
        {
            var control = Element(window, id); var r = control.Current.BoundingRectangle;
            var valid = !r.IsEmpty && r.Width > 1 && r.Height > 1 && !control.Current.IsOffscreen && r.Left >= bounds.Left - 2 && r.Right <= bounds.Right + 2 && r.Top >= bounds.Top - 2 && r.Bottom <= bounds.Bottom + 2;
            Layouts.Add(new { surface, id, window = Rectangle(bounds), rectangle = Rectangle(r), offscreen = control.Current.IsOffscreen, valid });
            if (!valid) errors.Add(id + " is zero-sized, offscreen or outside its window");
        }
        // Scrollable content may intentionally be offscreen; inspect all visible buttons for horizontal overflow.
        foreach (AutomationElement button in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            // WPF scrollbar template RepeatButtons (PageUp/PageDown) can legitimately have zero track area.
            if (button.Current.IsOffscreen || (string.IsNullOrEmpty(button.Current.Name) && !button.Current.AutomationId.Contains('.'))) continue;
            var r = button.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || r.Left < bounds.Left - 2 || r.Right > bounds.Right + 2) errors.Add("Visible button overflow: " + button.Current.AutomationId + " / " + button.Current.Name);
        }
        if (errors.Count > 0) Record("layout." + surface, "failed", 0, string.Join("; ", errors));
    }
    private static void Screenshot(AutomationElement window, string name)
    {
        EnsureOwned(window); var hwnd = new IntPtr(window.Current.NativeWindowHandle); var pid = _app!.Id;
        Require(hwnd != IntPtr.Zero && Native.GetWindowThreadProcessId(hwnd, out var nativePid) != 0 && nativePid == pid, "Screenshot HWND does not belong to owned app.");
        Require(window.Current.FrameworkId == "WPF", "Screenshots are restricted to WPF app windows.");
        CaptureAppVisual(window, name);
        var path = Path.Combine(_output, name + ".png");
        var task = Task.Run(() => CaptureHwnd(hwnd, path));
        if (!task.Wait(4000)) { Screenshots.Add(new { name, status = "unavailable", reason = "PrintWindow exceeded four-second bound" }); return; }
        var pixels = task.Result ? AnalyzePixels(path) : null;
        bool valid = pixels is not null && pixels.StandardDeviation > 2 && pixels.DistinctColors > 16;
        Screenshots.Add(new { name, status = valid ? "saved" : "unavailable", reason = valid ? null : "PrintWindow returned a blank/unusable client area", path, processId = pid, method = "PrintWindow", pixels, rectangle = Rectangle(window.Current.BoundingRectangle) });
    }
    private static void CaptureAppVisual(AutomationElement window, string name)
    {
        string type = Find(window, "capture.title") is not null ? "CaptureWindow" :
            Find(window, "resume.collapse") is not null ? "ResumeStrip" :
            Find(window, "prompt.next") is not null ? "ProgressWindow" :
            Find(window, "resources.openSelected") is not null ? "ResourceResumeWindow" :
            Find(window, "help.close") is not null ? "HelpWindow" :
            Find(window, "main.capture") is not null ? "MainWindow" : "Dialog-" + string.Concat(window.Current.Name.Split(Path.GetInvalidFileNameChars()));
        var source = Path.Combine(DiagnosticDirectories[_app!.Id], type + ".png");
        var requested = DateTime.UtcNow; string destination = Path.Combine(_output, name + "-visual.png");
        try
        {
            Await(() => File.Exists(source) && File.GetLastWriteTimeUtc(source) >= requested, "fresh app-owned visual " + type, 3500);
            File.Copy(source, destination, true); var pixels = AnalyzePixels(destination);
            bool valid = pixels.StandardDeviation > 2 && pixels.DistinctColors > 16;
            Screenshots.Add(new { name, status = valid ? "saved" : "unavailable", path = destination, processId = _app.Id, method = "App-owned WPF RenderTargetBitmap (visual tree, not desktop screenshot)", pixels });
        }
        catch (Exception ex) { Screenshots.Add(new { name, status = "unavailable", method = "App-owned WPF RenderTargetBitmap", reason = ex.Message }); }
    }
    private sealed record PixelEvidence(int Width, int Height, double Mean, double StandardDeviation, int DistinctColors);
    private static PixelEvidence AnalyzePixels(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var source = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var data = new byte[stride * height]; source.CopyPixels(data, stride, 0);
        long samples = 0; double sum = 0, sumSquared = 0; var colors = new HashSet<int>();
        // Exclude titlebar/borders: nonblank native chrome must not hide an empty WPF client.
        for (int y = 50; y < height - 20; y += 2)
            for (int x = 20; x < width - 20; x += 2)
            {
                int i = y * stride + x * 4; double value = (data[i] + data[i + 1] + data[i + 2]) / 3.0;
                samples++; sum += value; sumSquared += value * value;
                colors.Add(data[i] | data[i + 1] << 8 | data[i + 2] << 16);
            }
        double mean = samples == 0 ? 0 : sum / samples;
        return new PixelEvidence(width, height, mean, samples == 0 ? 0 : Math.Sqrt(Math.Max(0, sumSquared / samples - mean * mean)), colors.Count);
    }
    private static bool CaptureHwnd(IntPtr hwnd, string path)
    {
        if (!Native.GetWindowRect(hwnd, out var rect)) return false;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return false;
        var source = Native.GetDC(hwnd); var dc = Native.CreateCompatibleDC(source); var bitmap = Native.CreateCompatibleBitmap(source, width, height); var old = Native.SelectObject(dc, bitmap);
        try
        {
            if (!Native.PrintWindow(hwnd, dc, 2)) return false;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output); return true;
        }
        finally { Native.SelectObject(dc, old); Native.DeleteObject(bitmap); Native.DeleteDC(dc); Native.ReleaseDC(hwnd, source); }
    }
    private static string IntegrationStatus()
    {
        Invoke(MainWindow(), "main.settings"); var settings = WindowWith("settings.integration");
        string status = Element(settings, "settings.integration").Current.Name;
        Screenshot(settings, "settings-" + Screenshots.Count);
        Invoke(settings, "settings.close"); Await(() => FindWindow("settings.integration") is null, "settings closed"); return status;
    }
    private static void Focus(AutomationElement window, string id)
    {
        EnsureOwned(window); var hwnd = new IntPtr(window.Current.NativeWindowHandle); Native.SetForegroundWindow(hwnd); Element(window, id).SetFocus();
        try { Await(() => Native.GetForegroundWindow() == hwnd, "owned foreground before SendInput", 2000); }
        catch (TimeoutException) { throw new SkipException("Windows denied foreground activation; no shortcut sent to an unrelated window."); }
    }
    private static void SendChord(params ushort[] keys)
    {
        foreach (var key in keys) if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) throw new SkipException("User is holding a shortcut key; synthetic input skipped.");
        var inputs = keys.Select(k => Native.Key(k, false)).Concat(keys.Reverse().Select(k => Native.Key(k, true))).ToArray();
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
        {
            var releases = keys.Reverse().Select(k => Native.Key(k, true)).ToArray(); Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Native.INPUT>());
            throw new SkipException("SendInput blocked or partial (UIPI/noninteractive desktop), error " + Marshal.GetLastWin32Error());
        }
    }
    private sealed class SkipException(string message) : Exception(message);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUTUNION data; }
        [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        internal static INPUT Key(ushort key, bool up) => new() { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { vk = key, flags = up ? 2u : 0u } } };
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    }
}
