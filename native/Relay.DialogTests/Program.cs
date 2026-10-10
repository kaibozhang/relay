using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Relay.Core;
using Relay.Desktop;
using Relay.Desktop.Services;

// Run from the repository root: dotnet run --project native/Relay.DialogTests
// Real in-process WPF tests, not UIA and not mocks. Exit 0 = all passed,
// 1 = failure, 77 = explicit environment skip, 124 = bounded-wait timeout.
// No child processes, registry registration, clipboard, screenshots, or resource launches.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    private static int _failed;
    private static int _timedOut;
    private static bool _mainClosed;
    private static MainWindow _main = null!;
    private static byte[] _diskBefore = null!;
    private static string _dataPath = "";

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"Relay.DialogTests PID={Environment.ProcessId}; child processes: none");
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return Skip("Windows interactive desktop required.");
        var desktop = Native.OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero)
            return Skip($"Input desktop unavailable (Win32 {Marshal.GetLastWin32Error()}).");
        Native.CloseDesktop(desktop);
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(root, "native", "Relay.Desktop", "Relay.Desktop.csproj")))
            return Skip("Run from the repository root; refusing to guess a vault location.");
        var vault = Path.Combine(root, "artifacts", "dialog-tests-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("Isolated vault: " + vault);
        using var deadline = new Timer(_ => Timeout("Suite exceeded 90 seconds; isolated vault retained: " + vault),
            null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
        Application? app = null;
        try
        {
            Directory.CreateDirectory(vault);
            var store = new VaultStore(vault);
            var seed = store.Load();
            seed.Settings.CaptureHotkey = "Ctrl+Alt+Shift+J";
            seed.Settings.ResumeHotkey = "Ctrl+Alt+Shift+K";
            seed.Settings.CloseToTray = false;
            seed.Cards.Add(new RelayCard { Title = "Dialog test seed", NextAction = "Original next action" });
            store.Save(seed);
            _dataPath = store.DataPath;
            _diskBefore = File.ReadAllBytes(_dataPath);
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _main = new MainWindow(vault);
            app.MainWindow = _main;
            _main.Closed += (_, _) => _mainClosed = true;
            _main.Show();
            _main.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(_main.IsLoaded && _main.IsVisible, "MainWindow must be really loaded and visible");
            var integration = Field<WindowsIntegration>(_main, "_integration");
            Check(integration.CaptureHotkeyText == seed.Settings.CaptureHotkey &&
                  integration.ResumeHotkeyText == seed.Settings.ResumeHotkey, "Only isolated test hotkeys configured");
            Console.WriteLine("Native integration: " + integration.Status);
            // These tests do not press or require global hotkeys. Native affordance failures
            // are reported, never mistaken for coverage of registration/tray behavior.
            if (!integration.CaptureHotkeyRegistered || !integration.ResumeHotkeyRegistered)
                Console.WriteLine("SKIP native hotkey registration coverage: alternate keys unavailable; dialog tests continue.");

            Run("Quit cancels capture discard and preserves main/draft", QuitCapture);
            Run("Quit cancels progress discard and preserves main/draft", QuitProgress);
            Run("Capture required next action, false/throw retention, successful retry", CaptureRetry);
            Run("Progress optional checkpoint, no-change rejection, false/throw retention, successful retry", ProgressRetry);
            Run("ResumeLatest preserves dirty capture/progress without mutating document", ResumeLatestModalGuard);
            Run("Main.Edit rejects stale fingerprint and preserves new checkpoint", StaleEdit);
            Check(File.ReadAllBytes(_dataPath).SequenceEqual(_diskBefore), "Test callbacks must not change persisted seed vault");
        }
        catch (Exception error)
        {
            _failed++;
            Console.Error.WriteLine("FAIL suite/setup: " + error);
        }
        finally
        {
            try
            {
                if (app != null)
                {
                    foreach (var window in app.Windows.Cast<Window>().Where(w => w != _main).ToArray())
                        Discard(window);
                    if (_main != null)
                    {
                        // Read fields for lifecycle cleanup, never replace readonly services/document.
                        Field<DispatcherTimer>(_main, "_clock").Stop();
                        foreach (var field in typeof(MainWindow).GetFields(Private))
                            if (field.GetValue(_main) is IDisposable service) service.Dispose();
                        if (!_mainClosed) Call(_main, "Quit"); // Main.Close shuts down WPF: final action only.
                    }
                    if (!app.Dispatcher.HasShutdownStarted) app.Shutdown();
                }
                // Delete only the exact GUID directory created by this invocation.
                if (Path.GetDirectoryName(vault) != Path.Combine(root, "artifacts") ||
                    !Path.GetFileName(vault).StartsWith("dialog-tests-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing unexpected cleanup target: " + vault);
                if (Directory.Exists(vault)) Directory.Delete(vault, true);
                Console.WriteLine("Cleanup: native services disposed; own windows closed; isolated vault removed.");
            }
            catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL cleanup: " + error); }
        }
        Console.WriteLine($"RESULT: {_passed} passed; {_failed} failed.");
        return Volatile.Read(ref _timedOut) != 0 ? 124 : _failed == 0 ? 0 : 1;
    }

    private static int Skip(string reason) { Console.WriteLine("SKIP suite: " + reason); return 77; }
    private static void Timeout(string reason)
    {
        Interlocked.Exchange(ref _timedOut, 1);
        Console.Error.WriteLine("FAIL TIMEOUT: " + reason);
        Console.Error.Flush();
        Environment.Exit(124); // Own process only; prevents orphaned modal windows on an unresponsive dispatcher.
    }
    private static void Run(string name, Action action)
    {
        if (_mainClosed || _main.Dispatcher.HasShutdownStarted)
        { _failed++; Console.Error.WriteLine("FAIL " + name + ": main/dispatcher already shut down"); return; }
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(object target, string name) => (T)(GetField(target, name).GetValue(target)
        ?? throw new InvalidOperationException("Null field: " + name));
    private static FieldInfo GetField(object target, string name) => target.GetType().GetField(name, Private)
        ?? throw new MissingFieldException(target.GetType().FullName, name);
    private static void SetDialog(string name, Window? value) => GetField(_main, name).SetValue(_main, value);
    private static void Call(object target, string method, params object[] args)
    {
        try { (target.GetType().GetMethod(method, Private) ?? throw new MissingMethodException(method)).Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static TextBox Text(object target, string name) => Field<TextBox>(target, name);
    private static string DocumentJson() => JsonSerializer.Serialize(Field<VaultDocument>(_main, "_document"));
    private static void MainRetained()
    {
        Check(!_mainClosed && _main.IsVisible, "Canceled discard must keep main open and visible");
        Check(!Field<bool>(_main, "_quitting"), "Canceled discard must not mark main as quitting");
    }

    // ShowDialog supplies the actual nested dispatcher frame needed by DialogResult=true.
    // Assertions run in that frame; exceptions are captured, cleaned up, then rethrown outside it.
    private static bool? Modal(Window dialog, Action body)
    {
        Exception? failure = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { Check(dialog.IsVisible, "Modal dialog should be visible"); body(); }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) Discard(dialog); }
        }));
        var result = dialog.ShowDialog();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
    private static string DiscardCaption(Window dialog) => dialog is CaptureWindow ? "保留你的下一步" : "保留你的进展";
    private static void Respond(Window owner, string caption, int command, Action action)
    {
        using var responder = new MessageResponder(owner, caption, command);
        action();
        Check(responder.Sent == 1, "Expected exactly one own-process MessageBox: " + caption);
    }
    private static void Discard(Window dialog)
    {
        if (!dialog.IsVisible) return;
        // Arm even for clean dialogs; a successful close needs no confirmation.
        using var responder = new MessageResponder(dialog, DiscardCaption(dialog), 6);
        dialog.Close();
        Check(!dialog.IsVisible, "Explicit Yes discard must close dialog");
    }
    private static void QuitCapture()
    {
        var before = DocumentJson();
        var dialog = new CaptureWindow(null) { Owner = _main };
        SetDialog("_capture", dialog);
        try
        {
            Modal(dialog, () =>
            {
                Text(dialog, "_next").Text = "Unsaved capture next action";
                Text(dialog, "_context").Text = "Capture draft retained";
                Respond(dialog, DiscardCaption(dialog), 7, () => Call(_main, "Quit"));
                MainRetained();
                Check(dialog.IsVisible && ReferenceEquals(Field<CaptureWindow>(_main, "_capture"), dialog), "Capture remains tracked");
                Check(dialog.ResultCard == null && Text(dialog, "_context").Text == "Capture draft retained", "Capture draft unchanged");
                Check(DocumentJson() == before, "Canceled capture quit must not mutate document");
                Respond(dialog, DiscardCaption(dialog), 6, dialog.Close);
                Check(!dialog.IsVisible && dialog.ResultCard == null, "Yes discards without saving");
            });
        }
        finally { SetDialog("_capture", null); }
        MainRetained();
    }
    private static void QuitProgress()
    {
        var before = DocumentJson();
        var calls = 0;
        var dialog = new ProgressWindow(_main, "Test progress", "Next", (_, _) => { calls++; return true; });
        SetDialog("_progress", dialog);
        try
        {
            Modal(dialog, () =>
            {
                Text(dialog, "_progress").Text = "Unsaved checkpoint";
                Text(dialog, "_next").Text = "Unsaved next step";
                Respond(dialog, DiscardCaption(dialog), 7, () => Call(_main, "Quit"));
                MainRetained();
                Check(dialog.IsVisible && ReferenceEquals(Field<ProgressWindow>(_main, "_progress"), dialog), "Progress remains tracked");
                Check(Text(dialog, "_progress").Text == "Unsaved checkpoint" && Text(dialog, "_next").Text == "Unsaved next step", "Progress draft unchanged");
                Check(calls == 0 && DocumentJson() == before, "Canceled progress quit must not save");
                Respond(dialog, DiscardCaption(dialog), 6, dialog.Close);
                Check(!dialog.IsVisible && calls == 0, "Yes discards progress without callback");
            });
        }
        finally { SetDialog("_progress", null); }
        MainRetained();
    }
    private static void CaptureRetry()
    {
        var calls = 0;
        var dialog = new CaptureWindow(null) { Owner = _main };
        dialog.SaveHandler = card =>
        {
            calls++;
            Check(card.NextAction == "Valid next action", "Callback receives trimmed next action");
            return calls switch { 1 => false, 2 => throw new IOException("injected capture persistence failure"), _ => true };
        };
        var result = Modal(dialog, () =>
        {
            Text(dialog, "_context").Text = "Keep capture context";
            Text(dialog, "_next").Text = "  ";
            Call(dialog, "Save");
            Check(calls == 0 && dialog.IsVisible && dialog.ResultCard == null, "Required next action blocks callback");
            Check(Field<TextBlock>(dialog, "_message").Text.Length > 0, "Validation is visible to user");
            Text(dialog, "_next").Text = "  Valid next action  ";
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Call(dialog, "Save");
                Check(calls == attempt && dialog.IsVisible && dialog.ResultCard == null, "Rejected capture save retains dialog and null result");
                Check(Text(dialog, "_next").Text == "  Valid next action  " && Text(dialog, "_context").Text == "Keep capture context", "Failed save retains exact input");
                Check(!Field<bool>(dialog, "_saved"), "Failed capture save must not mark saved");
            }
            Check(Field<TextBlock>(dialog, "_message").Text.Contains("injected capture"), "Exception is shown inline");
            Call(dialog, "Save");
        });
        Check(result == true && !dialog.IsVisible && calls == 3 && dialog.ResultCard?.NextAction == "Valid next action", "Retry succeeds and returns card");
    }
    private static void ProgressRetry()
    {
        foreach (var progressInput in new[] { "", "  ", "  Valid progress  " })
        {
            var calls = 0;
            var dialog = new ProgressWindow(_main, "Retry checkpoint", "  Original next  ", (progress, next) =>
            {
                calls++;
                Check(progress == progressInput.Trim() && next == "Valid next", "Callback receives trimmed optional progress and next action");
                return calls switch { 1 => false, 2 => throw new IOException("injected progress persistence failure"), _ => true };
            });
            var result = Modal(dialog, () =>
            {
                Check(Text(dialog, "_next").Text == "  Original next  ", "Next action starts from current value");
                var form = (Panel)Text(dialog, "_next").Parent;
                Check(form.Children.IndexOf(Text(dialog, "_next")) < form.Children.IndexOf(Text(dialog, "_progress")), "Next action precedes optional progress");
                foreach (var unchangedNext in new[] { "  Original next  ", "Original next" })
                {
                    Text(dialog, "_next").Text = unchangedNext;
                    Text(dialog, "_progress").Text = "  ";
                    Call(dialog, "Save");
                    Check(calls == 0 && dialog.IsVisible && !Field<bool>(dialog, "_saved"), "Blank progress and normalized unchanged next never report success or call save");
                    Check(Field<TextBlock>(dialog, "_error").Visibility == Visibility.Visible &&
                          Field<TextBlock>(dialog, "_error").Text == "下一步没有变化；修改后再保存，或关闭。", "No-change explanation is shown inline");
                }
                Text(dialog, "_progress").Text = progressInput;
                Text(dialog, "_next").Text = "  ";
                Call(dialog, "Save");
                Check(calls == 0 && dialog.IsVisible, "Required next action blocks save even with optional progress");
                Check(Field<TextBlock>(dialog, "_error").Visibility == Visibility.Visible &&
                      Field<TextBlock>(dialog, "_error").Text == "请留下接下来的一个动作。", "Required-next validation is shown inline");
                Text(dialog, "_next").Text = "  Valid next  ";
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    Call(dialog, "Save");
                    Check(calls == attempt && dialog.IsVisible && !Field<bool>(dialog, "_saved"), "Rejected progress save retains dialog");
                    Check(Text(dialog, "_progress").Text == progressInput && Text(dialog, "_next").Text == "  Valid next  ", "Optional progress and next input survive failure exactly");
                    Check(!Field<bool>(dialog, "_saving") && Field<Button>(dialog, "_confirm").IsEnabled, "Retry guard/button restored");
                }
                Check(Field<TextBlock>(dialog, "_error").Text.Contains("injected progress"), "Progress exception shown inline");
                Call(dialog, "Save");
            });
            Check(result == true && !dialog.IsVisible && calls == 3, "Changed-next retry succeeds with blank or provided progress");
        }
        var checkpointCalls = 0;
        var checkpointDialog = new ProgressWindow(_main, "Checkpoint only", "Original next", (progress, next) =>
        {
            checkpointCalls++;
            Check(progress == "Valid progress" && next == "Original next", "Progress may be recorded without changing next action");
            return true;
        });
        var checkpointResult = Modal(checkpointDialog, () =>
        {
            Text(checkpointDialog, "_progress").Text = "  Valid progress  ";
            Call(checkpointDialog, "Save");
        });
        Check(checkpointResult == true && checkpointCalls == 1, "Nonempty progress with unchanged next still saves");
    }
    private static void ResumeLatestModalGuard()
    {
        foreach (var capture in new[] { true, false })
        {
            var before = Encoding.UTF8.GetBytes(DocumentJson());
            var diskBefore = File.ReadAllBytes(_dataPath);
            var card = Field<VaultDocument>(_main, "_document").Cards.Single();
            var countBefore = card.ResumeCount;
            var statusBefore = card.Status;
            var calls = 0;
            Window dialog = capture
                ? new CaptureWindow(null) { Owner = _main, SaveHandler = _ => { calls++; return true; } }
                : new ProgressWindow(_main, "Modal guard checkpoint", "Original next", (_, _) => { calls++; return true; });
            var field = capture ? "_capture" : "_progress";
            SetDialog(field, dialog);
            try
            {
                Modal(dialog, () =>
                {
                    Text(dialog, "_next").Text = "Unsaved next action must survive ResumeLatest";
                    var draftField = capture ? "_context" : "_progress";
                    Text(dialog, draftField).Text = "Unsaved modal draft";
                    Check(Field<bool>(dialog, "_dirty"), "Guard exercised with a dirty draft");
                    _main.ResumeLatest();
                    MainRetained();
                    Check(dialog.IsVisible && ReferenceEquals(GetField(_main, field).GetValue(_main), dialog), "ResumeLatest retains tracked modal");
                    Check(Text(dialog, "_next").Text == "Unsaved next action must survive ResumeLatest" &&
                          Text(dialog, draftField).Text == "Unsaved modal draft", "ResumeLatest retains exact draft inputs");
                    Check(calls == 0 && !Field<bool>(dialog, "_saved"), "ResumeLatest never invokes save or marks draft saved");
                    if (dialog is CaptureWindow editor) Check(editor.ResultCard == null, "Capture result remains null");
                    Check(Encoding.UTF8.GetBytes(DocumentJson()).SequenceEqual(before), "ResumeLatest leaves entire document byte-identical during modal");
                    var current = Field<VaultDocument>(_main, "_document").Cards.Single();
                    Check(current.ResumeCount == countBefore && current.Status == statusBefore, "Resume count/status unchanged");
                    Check(File.ReadAllBytes(_dataPath).SequenceEqual(diskBefore), "ResumeLatest leaves vault bytes unchanged");
                    Check(GetField(_main, "_strip").GetValue(_main) == null, "ResumeLatest must not create a resume strip behind modal");
                    Respond(dialog, DiscardCaption(dialog), 6, dialog.Close);
                    Check(!dialog.IsVisible && calls == 0, "Explicit Yes cleanup discards without saving");
                });
            }
            finally { SetDialog(field, null); }
            MainRetained();
        }
    }

    private static void StaleEdit()
    {
        // Read the real detached in-memory document. Only this scenario intentionally mutates
        // its card during the modal edit; never replace _document or write the user's vault.
        var document = Field<VaultDocument>(_main, "_document");
        var card = document.Cards.Single();
        var originalNext = card.NextAction;
        var checkpoint = new Checkpoint { Text = "New checkpoint while editor is open" };
        Exception? failure = null;
        _main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            CaptureWindow? editor = null;
            try
            {
                editor = Field<CaptureWindow>(_main, "_capture");
                Check(editor.IsVisible, "Main.Edit really opened modal editor");
                Text(editor, "_next").Text = "Stale replacement must not win";
                card.Checkpoints.Add(checkpoint); // Keep UpdatedAt unchanged: full fingerprint must detect this.
                Respond(editor, "编辑冲突", 1, () => Call(editor, "Save"));
                Check(editor.IsVisible && editor.ResultCard == null, "Stale editor stays open with null ResultCard");
                Check(Text(editor, "_next").Text == "Stale replacement must not win", "Stale draft retained for recovery");
                Check(card.NextAction == originalNext && card.Checkpoints.Contains(checkpoint), "New checkpoint and current action preserved");
                Check(Field<VaultDocument>(_main, "_document").Cards.Single().Checkpoints.Contains(checkpoint), "Live document still contains new checkpoint");
            }
            catch (Exception error) { failure = error; }
            finally { if (editor?.IsVisible == true) Discard(editor); }
        }));
        Call(_main, "Edit", card.Id);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        Check(GetField(_main, "_capture").GetValue(_main) == null, "Main.Edit releases dialog reference after discard");
        Check(card.Checkpoints.Contains(checkpoint) && card.NextAction == originalNext, "New checkpoint survives editor cleanup");
        MainRetained();
    }

    private sealed class MessageResponder : IDisposable
    {
        private readonly IntPtr _owner;
        private readonly string _caption;
        private readonly int _command;
        private readonly Timer _timer;
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(8);
        private int _sent;
        private int _disposed;
        public int Sent => Volatile.Read(ref _sent);
        public MessageResponder(Window owner, string caption, int command)
        {
            _owner = new WindowInteropHelper(owner).Handle;
            Check(_owner != IntPtr.Zero, "MessageBox owner must have a real HWND");
            _caption = caption; _command = command;
            _timer = new Timer(Tick, null, 25, 25);
        }
        private void Tick(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (DateTime.UtcNow >= _deadline) { Timeout("Own MessageBox did not complete: " + _caption); return; }
            if (Sent != 0) return;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var pid);
                if (pid != (uint)Environment.ProcessId || !Native.IsWindowVisible(window) ||
                    Native.GetWindow(window, 4) != _owner) return true; // GW_OWNER, not foreground window.
                var name = new StringBuilder(256);
                Native.GetClassName(window, name, name.Capacity);
                if (name.ToString() != "#32770") return true;
                name.Clear(); Native.GetWindowText(window, name, name.Capacity);
                var button = Native.GetDlgItem(window, _command);
                if (name.ToString() != _caption) return true;
                // MB_OK can expose its sole OK button as IDCANCEL (2). Use that
                // actual native ID only for this known OK-only conflict message.
                var nativeCommand = _command;
                if (_command == 1 && button == IntPtr.Zero)
                { button = Native.GetDlgItem(window, 2); nativeCommand = 2; }
                if (button == IntPtr.Zero || !Native.IsWindowEnabled(button)) return true;
                if (Interlocked.CompareExchange(ref _sent, 1, 0) != 0) return false;
                if (!Native.PostMessage(window, 0x0111, (IntPtr)nativeCommand, button))
                    Timeout("WM_COMMAND failed for own MessageBox (Win32 " + Marshal.GetLastWin32Error() + ")");
                return false;
            }, IntPtr.Zero);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            using var drained = new ManualResetEvent(false);
            if (_timer.Dispose(drained)) drained.WaitOne(TimeSpan.FromSeconds(2));
        }
    }
    private static class Native
    {
        internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern IntPtr GetDlgItem(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    }
}
