using System;
using System.Collections.Generic;
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
using System.Windows.Threading;
using Relay.Core;
using Relay.Desktop;
using Relay.Desktop.Services;

// Run from repository root: dotnet run --project native/Relay.DraftDialogTests
// Real STA WPF ShowDialog, private fields/methods accessed by reflection, real stores/file locks.
// No production hooks, UIA, registry, clipboard, network, resource launch, or native integration.
// Private _keepDraft is set ONLY in harness cleanup, after assertions, to avoid discard MessageBoxes.
// These tests do not cover debounce timing, recovery-picker interaction, or native MessageBoxes.
// Exit 0 = all passed, 1 = failed, 77 = environment skip, 124 = hard suite timeout.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string _root = "";
    private static int _passed, _failed;
    private static Window _owner = null!;
    private static MainWindow? _main;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return Skip("Windows interactive desktop required");
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return Skip("Input desktop unavailable: " + Marshal.GetLastWin32Error());
        CloseDesktop(desktop);
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(workspace, "native", "Relay.Desktop", "Relay.Desktop.csproj")))
            return Skip("Run from repository root; refusing to guess fixture location");
        _root = Path.Combine(workspace, "artifacts", "draft-dialog-tests-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("Fixture root: " + _root);
        Console.WriteLine("Reflection drives actual new-capture fields/private methods; no production modifications.");
        using var deadline = new Timer(_ =>
        {
            Console.Error.WriteLine("TIMEOUT: suite exceeded 90 seconds; fixtures retained: " + _root);
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        Application? app = null;
        try
        {
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "STA thread required");
            Directory.CreateDirectory(_root);
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _owner = new Window { Title = "Relay isolated draft tests", Width = 260, Height = 100, ShowInTaskbar = false };
            app.MainWindow = _owner;
            _owner.Show();
            Run("raw fields persist, unchanged save caches, KeepDraft freshly persists after external deletion", RawAndKeep);
            Run("locked SaveDraft and KeepDraft retain visible dialog and exact unsaved text", LockedSave);
            Run("locked opt-out rechecks checkbox and privacy warning survives later save", FailedOptOut);
            Run("successful opt-out deletes draft and prevents later draft writes", Disable);
            Run("successful formal SaveHandler runs before cleanup and removes draft", FormalSave);
            Run("unshown MainWindow retains divergent ID collision and corrupt evidence without document mutation", Collision);
        }
        catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL setup: " + ex); }
        finally
        {
            try
            {
                if (app != null)
                {
                    foreach (var window in app.Windows.Cast<Window>().Where(w => w != _main).ToArray())
                        if (window is CaptureWindow capture) Cleanup(capture); else window.Close();
                    if (_main != null)
                    {
                        Check(Get(_main, "_integration") == null && !Field<DispatcherTimer>(_main, "_clock").IsEnabled,
                            "MainWindow cleanup must have no native integration/timer");
                        Set(_main, "_quitting", true);
                        _main.Close(); // MainWindow.Closed itself requests explicit Application shutdown.
                    }
                    app.Shutdown();
                }
            }
            catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL window cleanup: " + ex); }
            try
            {
                var expectedParent = Path.Combine(workspace, "artifacts");
                Check(Path.GetDirectoryName(Path.GetFullPath(_root)) == expectedParent &&
                    Path.GetFileName(_root).StartsWith("draft-dialog-tests-", StringComparison.Ordinal), "unsafe cleanup path");
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
                Console.WriteLine("Owned fixtures removed: " + !Directory.Exists(_root));
            }
            catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL fixture cleanup: " + ex); }
        }
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} skipped=0");
        return _failed == 0 ? 0 : 1;
    }

    private static void RawAndKeep()
    {
        var store = Store("raw-keep");
        var dialog = New(store);
        var calls = 0;
        dialog.SaveHandler = _ => { calls++; return true; };
        string path = "";
        var result = Modal(dialog, () =>
        {
            Inputs(dialog, "  Raw title  ", "  Next action\nsecond line  ", "  context 😀\n尾  ");
            var title = Text(dialog, "_title").Text;
            var next = Text(dialog, "_next").Text;
            var context = Text(dialog, "_context").Text;
            Check(Call<bool>(dialog, "SaveDraft"), "initial save failed");
            var saved = store.List().Single().Card;
            Check(saved.Title == title && saved.NextAction == next && saved.Context == context, "raw field text was trimmed or changed");
            Check(saved.Id == Field<RelayCard>(dialog, "_draft").Id, "new draft ID mismatch");
            path = DraftPath(store, saved.Id);
            var before = File.ReadAllBytes(path);
            var signature = Field<string>(dialog, "_lastDraftSignature");
            Check(!string.IsNullOrEmpty(signature), "signature not cached");
            // A no-op save must succeed even while reads/replacements are denied by an exclusive lock.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(Call<bool>(dialog, "SaveDraft"), "unchanged save unexpectedly touched locked file");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "cached save rewrote file");
            DeleteOwnedFile(path);
            Check(!File.Exists(path), "external deletion did not remove file");
            Call(dialog, "KeepDraft");
            Check(!dialog.IsVisible && Field<bool>(dialog, "_keepDraft"), "KeepDraft did not naturally close dialog");
            Check(File.Exists(path), "KeepDraft trusted cache instead of fresh persistence");
            var kept = store.List().Single().Card;
            Check(kept.Title == title && kept.NextAction == next && kept.Context == context, "kept draft differs from raw fields");
            Check(calls == 0 && dialog.ResultCard == null && !Field<bool>(dialog, "_saved"), "KeepDraft formally submitted");
        });
        Check(result == false && File.Exists(path) && calls == 0, "KeepDraft modal outcome must be nonformal false with file retained");
    }

    private static void LockedSave()
    {
        var store = Store("locked-save");
        var dialog = New(store);
        var calls = 0;
        dialog.SaveHandler = _ => { calls++; return true; };
        Modal(dialog, () =>
        {
            Inputs(dialog, "before", "before next", "before context");
            Check(Call<bool>(dialog, "SaveDraft"), "seed draft failed");
            var path = DraftPath(store, Field<RelayCard>(dialog, "_draft").Id);
            var before = File.ReadAllBytes(path);
            Inputs(dialog, " after title ", " after next ", " after context 😀 ");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(!Call<bool>(dialog, "SaveDraft"), "locked save unexpectedly succeeded");
                Retained(dialog, " after title ", " after next ", " after context 😀 ");
                Check(Field<TextBlock>(dialog, "_draftStatus").Text.Contains("草稿未能暂存"), "save error absent");
                Call(dialog, "KeepDraft");
                Retained(dialog, " after title ", " after next ", " after context 😀 ");
                Check(!Field<bool>(dialog, "_keepDraft"), "failed keep marked draft kept");
            }
            Check(File.ReadAllBytes(path).SequenceEqual(before), "failed save changed prior disk draft");
            Check(calls == 0, "failed save invoked formal callback");
            Check(Call<bool>(dialog, "SaveDraft") && store.List().Single().Card.NextAction == " after next ", "save did not recover after unlock");
        });
    }

    private static void FailedOptOut()
    {
        var store = Store("failed-optout");
        var dialog = New(store);
        Modal(dialog, () =>
        {
            Inputs(dialog, "title", "next", "context");
            Check(Call<bool>(dialog, "SaveDraft"), "seed draft failed");
            var path = DraftPath(store, Field<RelayCard>(dialog, "_draft").Id);
            var before = File.ReadAllBytes(path);
            var checkbox = Field<CheckBox>(dialog, "_draftEnabled");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                checkbox.IsChecked = false;
                Call(dialog, "ChangeDraftOption");
                Check(checkbox.IsChecked == true, "failed delete left opt-out misleadingly unchecked");
                var warning = Field<string>(dialog, "_draftPrivacyWarning");
                Check(!string.IsNullOrWhiteSpace(warning) && Field<TextBlock>(dialog, "_draftStatus").Text.Contains(warning), "privacy warning absent");
                Retained(dialog, "title", "next", "context");
            }
            Check(File.ReadAllBytes(path).SequenceEqual(before), "failed opt-out changed/deleted file");
            Text(dialog, "_context").Text = "context after unlock";
            var retainedWarning = Field<string>(dialog, "_draftPrivacyWarning");
            Check(Call<bool>(dialog, "SaveDraft"), "subsequent save failed");
            Check(Field<string>(dialog, "_draftPrivacyWarning") == retainedWarning &&
                Field<TextBlock>(dialog, "_draftStatus").Text.Contains(retainedWarning), "later save hid failed-opt-out warning");
            Check(store.List().Single().Card.Context == "context after unlock" && checkbox.IsChecked == true, "autosave not still enabled");
        });
    }

    private static void Disable()
    {
        var store = Store("disable");
        var dialog = New(store);
        var calls = 0;
        dialog.SaveHandler = _ => { calls++; return true; };
        Modal(dialog, () =>
        {
            Inputs(dialog, "title", "next", "context");
            Check(Call<bool>(dialog, "SaveDraft"), "seed draft failed");
            var path = DraftPath(store, Field<RelayCard>(dialog, "_draft").Id);
            Field<CheckBox>(dialog, "_draftEnabled").IsChecked = false;
            Call(dialog, "ChangeDraftOption");
            Check(!File.Exists(path) && Get(dialog, "_draftPrivacyWarning") == null, "successful opt-out retained file/warning");
            Text(dialog, "_context").Text = "memory only";
            Check(!Call<bool>(dialog, "SaveDraft") && !Call<bool>(dialog, "SaveDraft"), "disabled SaveDraft accepted writes");
            Check(!File.Exists(path) && Directory.GetFiles(store.DirectoryPath).Length == 0, "disabled save wrote a file");
            Call(dialog, "KeepDraft");
            Retained(dialog, "title", "next", "memory only");
            Check(calls == 0 && !Field<bool>(dialog, "_keepDraft"), "disabled keep closed/submitted");
        });
    }

    private static void FormalSave()
    {
        var store = Store("formal");
        var vault = new VaultStore(Path.Combine(_root, "formal"));
        var doc = vault.Load();
        var dialog = New(store);
        var calls = 0;
        string path = "";
        dialog.SaveHandler = card =>
        {
            calls++;
            Check(File.Exists(path) && !Field<bool>(dialog, "_saved"), "draft removed before successful callback");
            Check(card.Title == "Title" && card.NextAction == "Next", "formal fields not normalized");
            doc.Cards.Add(card);
            vault.Save(doc);
            return true;
        };
        var result = Modal(dialog, () =>
        {
            Inputs(dialog, "  Title  ", "  Next  ", " raw context ");
            Check(Call<bool>(dialog, "SaveDraft"), "seed draft failed");
            path = DraftPath(store, Field<RelayCard>(dialog, "_draft").Id);
            Call(dialog, "Save");
            Check(!dialog.IsVisible && calls == 1 && dialog.ResultCard != null && Field<bool>(dialog, "_saved"), "formal save did not close successfully");
            Check(!File.Exists(path) && store.List().Count == 0, "successful formal save retained draft file");
            Check(!Field<DispatcherTimer>(dialog, "_draftTimer").IsEnabled, "draft timer still running after close");
        });
        Check(result == true && calls == 1, "successful formal modal result incorrect");
        var saved = new VaultStore(Path.Combine(_root, "formal")).Load().Cards.Single();
        Check(saved.Id == dialog.ResultCard!.Id && saved.Context == " raw context " && !File.Exists(path), "formal persisted result/cleanup mismatch");
    }

    private static void Collision()
    {
        var directory = Path.Combine(_root, "collision");
        var vault = new VaultStore(directory);
        var doc = vault.Load();
        var formal = new RelayCard { Title = "Formal title", NextAction = "Formal next", Context = "Formal context" };
        doc.Cards.Add(formal);
        vault.Save(doc);
        var store = new CaptureDraftStore(directory);
        store.Save(new RelayCard { Id = formal.Id, Title = "Divergent title", NextAction = "Divergent next", Context = "Never discard this" });
        var other = new RelayCard { Title = "Other draft", NextAction = "Other next" };
        store.Save(other);
        var corrupt = DraftPath(store, Guid.NewGuid().ToString("N"));
        File.WriteAllText(corrupt, "{ invalid fixture JSON");
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.ReadAllBytes(p), StringComparer.OrdinalIgnoreCase);
        _main = new MainWindow(directory); // Deliberately never Show/InitializeSystem/ShowDrafts.
        Check(!_main.IsLoaded && !_main.IsVisible && Get(_main, "_integration") == null, "unshown main initialized native integration");
        var document = Field<VaultDocument>(_main, "_document");
        var documentBefore = JsonSerializer.Serialize(document);
        for (var i = 0; i < 2; i++)
        {
            var drafts = Call<IReadOnlyList<CaptureDraft>>(_main, "RecoverableDrafts");
            Check(drafts.Count == 2 && drafts.Single(d => d.Card.Id == formal.Id).Card.Context == "Never discard this", "collision was filtered/changed");
            Check(drafts.Any(d => d.Card.Id == other.Id), "unrelated draft disappeared");
            Check(!string.IsNullOrWhiteSpace(Field<CaptureDraftStore>(_main, "_draftStore").Warning), "corrupt-file warning lost");
            Check(ReferenceEquals(document, Field<VaultDocument>(_main, "_document")) &&
                JsonSerializer.Serialize(document) == documentBefore, "formal document mutated during list");
            var after = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            Check(after.Length == before.Count && after.All(before.ContainsKey), "listing added/deleted fixture files");
            foreach (var file in before)
                Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value), "listing changed/deleted file: " + file.Key);
        }
        Check(Get(_main, "_integration") == null && !Field<DispatcherTimer>(_main, "_clock").IsEnabled, "listing initialized native integration");
    }

    private static CaptureDraftStore Store(string name) => new(Path.Combine(_root, name));
    private static CaptureWindow New(CaptureDraftStore store)
    {
        var dialog = new CaptureWindow(null) { Owner = _owner, ShowInTaskbar = false };
        dialog.EnableDraftRecovery(store);
        return dialog;
    }
    private static bool? Modal(CaptureWindow dialog, Action body)
    {
        Exception? failure = null;
        var ran = false;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ran = true;
            try { Check(dialog.IsVisible && dialog.IsLoaded, "dialog not actually shown"); body(); }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) Cleanup(dialog); }
        };
        bool? result;
        try { timer.Start(); result = dialog.ShowDialog(); }
        finally { timer.Stop(); if (dialog.IsVisible) Cleanup(dialog); }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        Check(ran, "modal test callback never ran");
        return result;
    }
    private static void Cleanup(CaptureWindow dialog)
    {
        Field<DispatcherTimer>(dialog, "_draftTimer").Stop();
        Set(dialog, "_keepDraft", true); // Harness-only cleanup, never an assertion of successful product behavior.
        dialog.Close();
    }
    private static void Inputs(CaptureWindow dialog, string title, string next, string context)
    {
        Text(dialog, "_title").Text = title;
        Text(dialog, "_next").Text = next;
        Text(dialog, "_context").Text = context;
    }
    private static void Retained(CaptureWindow dialog, string title, string next, string context) =>
        Check(dialog.IsVisible && dialog.ResultCard == null && !Field<bool>(dialog, "_saved") &&
            Field<bool>(dialog, "_dirty") && Text(dialog, "_title").Text == title &&
            Text(dialog, "_next").Text == next && Text(dialog, "_context").Text == context, "dialog/text not retained");
    private static string DraftPath(CaptureDraftStore store, string id) => Path.Combine(store.DirectoryPath, id + ".json");
    private static void DeleteOwnedFile(string path)
    {
        var full = Path.GetFullPath(path);
        Check(full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "refusing nonfixture deletion");
        File.Delete(full);
    }
    private static TextBox Text(object target, string name) => Field<TextBox>(target, name);
    private static FieldInfo Info(object target, string name) => target.GetType().GetField(name, Private) ?? throw new MissingFieldException(name);
    private static object? Get(object target, string name) => Info(target, name).GetValue(target);
    private static T Field<T>(object target, string name) => (T)(Get(target, name) ?? throw new InvalidOperationException("null field: " + name));
    private static void Set(object target, string name, object value) => Info(target, name).SetValue(target, value);
    private static object? Call(object target, string name)
    {
        try { return (target.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(name)).Invoke(target, null); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static T Call<T>(object target, string name) => (T)(Call(target, name) ?? throw new InvalidOperationException("null method result: " + name));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Run(string name, Action body)
    {
        try { body(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static int Skip(string reason) { Console.WriteLine("SKIP (not pass): " + reason); return 77; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
