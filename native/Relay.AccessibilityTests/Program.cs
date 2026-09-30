using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Relay.Core;
using Relay.Desktop;
using Relay.Desktop.Services;

// Run from repository root: dotnet run --project native/Relay.AccessibilityTests -c Release
// Ten source-behavior scenarios, not external UIA or end-to-end physical keyboard tests.
// Reflection reaches private Render*, ShowSettings, AddUrl and state for assertions/cleanup.
// No App.Start, clipboard, network, resource opening, OS high-contrast changes or other-app reads.
// Real MainWindow.Loaded initializes native integration with dedicated alternate hotkeys.
// Exit 0: passed; 1: assertion/setup failure; 77: noninteractive environment; 124: timeout.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainWindow _main = null!;
    private static string _cardId = "";
    private static int _passed, _failed;
    private static bool _searchOnly;
    private static ResourceDictionary _theme = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _searchOnly = args.SequenceEqual(new[] { "--search-only" });
        if (args.Length != 0 && !_searchOnly) { Console.Error.WriteLine("Only optional --search-only is supported."); return 1; }
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        { Console.WriteLine("SKIP: interactive Windows desktop required."); return 77; }
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(root, "native", "Relay.Desktop", "Relay.Desktop.csproj")))
        { Console.Error.WriteLine("Run from repository root; no guessed vault path."); return 1; }
        var vault = Path.Combine(root, "artifacts", "accessibility-tests-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine($"Relay.AccessibilityTests PID={Environment.ProcessId}; STA={Thread.CurrentThread.GetApartmentState()}; vault={vault}");
        using var deadline = Deadline("suite", 90);
        Application? app = null;
        try
        {
            var store = new VaultStore(vault);
            var seed = store.Load();
            seed.Settings.CaptureHotkey = "Ctrl+Alt+Shift+U";
            seed.Settings.ResumeHotkey = "Ctrl+Alt+Shift+I";
            seed.Settings.CloseToTray = false;
            seed.Settings.FocusMinutes = 25;
            var card = new RelayCard { Title = "Accessibility seed 中文", NextAction = "Continue isolated test", Status = "active" };
            _cardId = card.Id;
            seed.Cards.Add(card);
            store.Save(seed);
            var before = File.ReadAllBytes(store.DataPath);
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Relay;component/Theme.xaml", UriKind.Absolute) };
            app.Resources.MergedDictionaries.Add(_theme);
            _main = new MainWindow(vault);
            app.MainWindow = _main;
            _main.Show();
            Drain();
            Check(_main.IsVisible && _main.IsLoaded, "actual MainWindow is shown/loaded");
            var integration = Field<WindowsIntegration>(_main, "_integration");
            Check(integration.CaptureHotkeyText == seed.Settings.CaptureHotkey && integration.ResumeHotkeyText == seed.Settings.ResumeHotkey,
                "only dedicated alternate hotkeys configured");
            Console.WriteLine("Native integration (not a test of registration): " + integration.Status);

            Run("Compiled shared Theme and normal accent contrast", ThemeAndContrast);
            Run("Navigation invoke restores rebuilt button keyboard focus", Navigation);
            Run("Full Render preserves focused AutomationId", FullRender);
            Run("Selecting same card retains selection and rebuilt card focus", SameCard);
            Run("Detail focus timer start/pause retains rebuilt button focus", DetailFocus);
            Run("Search value/edit rebuild preserves TextBox focus and caret", Search);
            Run("Main search and capture fields expose Chinese names", Names);
            Run("Settings fields expose Chinese names in real modal", SettingsNames);
            Run("URL Cancel-focused Enter preview does not accept", UrlCancel);
            Run("URL input-focused Enter preview accepts exactly one reference", UrlAccept);

            Check(File.ReadAllBytes(store.DataPath).SequenceEqual(before), "seed vault bytes unchanged; timer state is separate");
            Console.WriteLine($"HighContrast current={SystemParameters.HighContrast}; SKIP HC runtime transitions/alternate palettes: not verified; OS state unchanged.");
            Console.WriteLine("LIMIT: no IME composition simulation; search uses in-process Value provider and TextBox edit APIs, not physical typing.");
            Console.WriteLine("LIMIT: URL tests raise PreviewKeyDown with own live HwndSource; Cancel is then explicitly invoked. Native Enter/default-button dispatch is not claimed.");
        }
        catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL setup/suite: " + error); }
        finally
        {
            try
            {
                if (app != null)
                {
                    foreach (var window in app.Windows.Cast<Window>().Where(w => w != _main).ToArray()) CloseOwn(window);
                    if (_main != null) _main.Close();
                    app.Shutdown();
                }
            }
            catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL cleanup: " + error.Message); }
        }
        var expected = _searchOnly ? 1 : 10;
        Console.WriteLine($"RESULT: {_passed}/{expected} passed; {_failed} failures. Isolated vault retained: {vault}");
        return _failed == 0 && _passed == expected ? 0 : 1;
    }

    private static void ThemeAndContrast()
    {
        Check(_theme[typeof(Button)] is Style && _theme[typeof(TextBox)] is Style && _theme[typeof(ToolTip)] is Style,
            "Relay pack URI loads compiled Button/TextBox/ToolTip resources");
        var button = Find<Button>(_main, "main.capture");
        button.ApplyTemplate();
        Check(ReferenceEquals(button.Style, _theme[typeof(Button)]), "real main button inherits compiled shared style");
        Check(button.Template.FindName("Frame", button) is Border, "shared Button template is instantiated");
        Check(ReferenceEquals(Find<TextBox>(_main, "main.search").Style, _theme[typeof(TextBox)]), "real search inherits shared TextBox style");
        Check(Math.Abs(Contrast(Colors.Black, Colors.White) - 21) < 0.000001, "WCAG sRGB contrast formula calibration");
        if (!SystemParameters.HighContrast)
        {
            var accent = ((SolidColorBrush)Ui.Accent).Color;
            Check(accent == Color.FromRgb(150, 81, 34), "normal accent is #965122");
            var paper = Contrast(accent, ((SolidColorBrush)Ui.Paper).Color);
            var surface = Contrast(accent, ((SolidColorBrush)Ui.Surface).Color);
            Console.WriteLine($"Normal accent contrast: paper={paper:F4}:1; surface={surface:F4}:1 (WCAG threshold 4.5:1)");
            Check(paper >= 4.5 && surface >= 4.5, "normal accent meets 4.5:1 on both backgrounds");
        }
        else Console.WriteLine("SKIP normal palette branch: current OS already in high contrast; no state change attempted.");
    }

    private static void Navigation()
    {
        var previous = Find<Button>(_main, "nav.all");
        Focus(previous);
        Invoke(previous);
        Drain();
        Check(Field<string>(_main, "_filter") == "all", "navigation handler changed filter");
        Check(!ReferenceEquals(previous, Find<Button>(_main, "nav.all")), "navigation actually rebuilt button");
        FocusId("nav.all");
    }

    private static void FullRender()
    {
        var previous = Find<Button>(_main, "nav.ready");
        Focus(previous);
        Call(_main, "Render");
        Drain();
        Check(!ReferenceEquals(previous, Find<Button>(_main, "nav.ready")), "full Render replaced focused control");
        FocusId("nav.ready");
    }

    private static void SameCard()
    {
        var previous = Find<Button>(_main, "card." + _cardId);
        Check(Field<string>(_main, "_selected") == _cardId, "seed card initially selected");
        Focus(previous);
        Invoke(previous);
        Drain();
        Check(Field<string>(_main, "_selected") == _cardId, "same card remains selected");
        Check(!ReferenceEquals(previous, Find<Button>(_main, "card." + _cardId)), "card button rebuilt");
        FocusId("card." + _cardId);
    }

    private static void DetailFocus()
    {
        var store = Field<FocusSessionStore>(_main, "_focusStore");
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var previous = Find<Button>(_main, "detail.focus");
                Focus(previous);
                Invoke(previous);
                Drain();
                Check(!ReferenceEquals(previous, Find<Button>(_main, "detail.focus")), "detail button rebuilt by timer action");
                FocusId("detail.focus");
                Check(store.Current?.CardId == _cardId && store.Current.Paused == (i == 1), "real timer start/pause state changed");
            }
        }
        finally { Call(_main, "StopFocus"); }
    }

    private static void Search()
    {
        var search = Find<TextBox>(_main, "main.search");
        Focus(search);
        var peer = new TextBoxAutomationPeer(search);
        var value = (IValueProvider)peer.GetPattern(PatternInterface.Value);
        SearchState("before Value.SetValue", search);
        value.SetValue("Accessibility seed");
        SearchState("after Value.SetValue synchronous", search);
        Drain();
        SearchState("after Value.SetValue idle", search);
        Check(search.Text == "Accessibility seed", "in-process Value provider changed actual search");
        Check(ReferenceEquals(search, Find<TextBox>(_main, "main.search")), "search itself not rebuilt");
        FocusId("main.search");
        search.Select(5, 0);
        Call(_main, "RenderList");
        Drain();
        Check(search.CaretIndex == 5 && search.SelectionLength == 0, "list rebuild leaves existing caret untouched");
        // Compare standard WPF text-edit semantics, not a fabricated assertion about physical typing/IME.
        var reference = new TextBox { Text = search.Text };
        reference.Select(5, 0);
        reference.SelectedText = "中";
        search.SelectedText = "中"; // Synchronously triggers production TextChanged -> RenderList.
        Drain();
        Check(search.Text == reference.Text && search.CaretIndex == reference.CaretIndex &&
            search.SelectionStart == reference.SelectionStart && search.SelectionLength == reference.SelectionLength,
            "TextChanged rebuild does not alter native TextBox edit caret/selection semantics");
        FocusId("main.search");
        search.Select(2, 3);
        Call(_main, "Render");
        Drain();
        Check(search.SelectionStart == 2 && search.SelectionLength == 3, "full Render preserves search selection");
        FocusId("main.search");
        search.Clear();
        Drain();
    }

    private static void Names()
    {
        ChineseName(Find<TextBox>(_main, "main.search"));
        var capture = new CaptureWindow(null) { Owner = _main };
        try
        {
            capture.Show(); Drain();
            foreach (var id in new[] { "capture.title", "capture.next", "capture.context" }) ChineseName(Find<TextBox>(capture, id));
        }
        finally { CloseOwn(capture); }
    }

    private static void SettingsNames() => Modal(_main, () => Call(_main, "ShowSettings"), window =>
    {
        foreach (var id in new[] { "settings.captureHotkey", "settings.resumeHotkey", "settings.focusMinutes" })
            ChineseName(Find<TextBox>(window, id));
        Invoke(Find<Button>(window, "settings.close"));
    });

    private static void UrlCancel() => WithCapture(capture =>
    {
        Modal(capture, () => Call(capture, "AddUrl"), prompt =>
        {
            var input = Find<TextBox>(prompt, "capture.urlInput");
            input.Text = "https://example.invalid/cancel";
            ChineseName(input);
            var cancel = Find<Button>(prompt, "capture.urlCancel");
            Focus(cancel);
            var enter = EnterPreview(prompt, cancel);
            Check(!enter.Handled, "prompt preview must leave Cancel Enter unhandled");
            Check(prompt.IsVisible && Field<RelayCard>(capture, "_draft").Resources.Count == 0, "Cancel-focused Enter did not Accept valid URL");
            Invoke(cancel);
        });
        Check(Field<RelayCard>(capture, "_draft").Resources.Count == 0, "Cancel leaves resource count zero");
    });

    private static void UrlAccept() => WithCapture(capture =>
    {
        Modal(capture, () => Call(capture, "AddUrl"), prompt =>
        {
            var input = Find<TextBox>(prompt, "capture.urlInput");
            input.Text = "https://example.invalid/accepted";
            Focus(input);
            var enter = EnterPreview(prompt, input);
            Check(enter.Handled, "input-focused Enter handled by prompt preview");
            Check(!prompt.IsVisible, "Accept closes URL prompt");
        });
        var resources = Field<RelayCard>(capture, "_draft").Resources;
        Check(resources.Count == 1 && resources[0].Kind == "url" && resources[0].Target == "https://example.invalid/accepted",
            "exactly one URL reference added, never opened");
    });

    private static void WithCapture(Action<CaptureWindow> action)
    {
        var capture = new CaptureWindow(null) { Owner = _main };
        try { capture.Show(); Drain(); action(capture); }
        finally { CloseOwn(capture); }
    }

    private static KeyEventArgs EnterPreview(Window window, UIElement target)
    {
        Check(Keyboard.Modifiers == ModifierKeys.None, "no physical modifiers held during Enter scenario");
        var source = PresentationSource.FromVisual(window) as HwndSource;
        Check(source != null && source.Handle != IntPtr.Zero, "Enter uses own live WPF HWND presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args;
    }

    private static void Modal(Window owner, Action open, Action<Window> inspect)
    {
        Exception? failure = null;
        bool visited = false;
        using var deadline = Deadline("own modal", 12);
        var pending = owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Window? modal = null;
            try
            {
                modal = Application.Current.Windows.Cast<Window>().Single(w => w.Owner == owner && w.IsVisible);
                visited = true;
                inspect(modal);
            }
            catch (Exception error) { failure = error; }
            finally { if (modal?.IsVisible == true) modal.Close(); }
        }));
        try { open(); }
        finally { if (pending.Status == DispatcherOperationStatus.Pending) pending.Abort(); }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        Check(visited, "real owned modal inspected during ShowDialog");
    }

    private static void CloseOwn(Window window)
    {
        // Cleanup only: bypass dirty-discard MessageBox rather than read/operate any native dialog.
        // No save/persistence callback is invoked; production assertions are finished first.
        if (window is CaptureWindow) GetField(window, "_dirty").SetValue(window, false);
        window.Close();
    }

    private static void SearchState(string stage, TextBox search)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        Console.WriteLine($"SEARCH {stage}: id={(focused == null ? "<null>" : AutomationProperties.GetAutomationId(focused))}; type={focused?.GetType().Name ?? "<null>"}; mainActive={_main.IsActive}; liveMainAncestor={focused != null && _main.IsAncestorOf(focused)}; mainKeyboardFocusWithin={_main.IsKeyboardFocusWithin}; searchIsFocused={search.IsFocused}; searchKeyboardFocused={search.IsKeyboardFocused}; caret={search.CaretIndex}; selection={search.SelectionStart},{search.SelectionLength}");
    }

    private static void Run(string name, Action action)
    {
        if (_searchOnly && action != Search) return;
        using var deadline = Deadline(name, 15); // Also bounds any unexpected production MessageBox.
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }

    private static Timer Deadline(string label, int seconds) => new(_ =>
    {
        Console.Error.WriteLine("TIMEOUT " + label + "; stopping only this test process; own vault retained.");
        Environment.Exit(124);
    }, null, TimeSpan.FromSeconds(seconds), System.Threading.Timeout.InfiniteTimeSpan);

    private static void Focus(FrameworkElement element)
    {
        var window = Window.GetWindow(element)!;
        window.Activate();
        element.BringIntoView();
        Drain();
        Keyboard.Focus(element);
        Drain();
        Check(window.IsActive && ReferenceEquals(Keyboard.FocusedElement, element), "real keyboard focus precondition: " + AutomationProperties.GetAutomationId(element));
    }
    private static void FocusId(string id)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var actualId = focused == null ? "<null>" : AutomationProperties.GetAutomationId(focused);
        var inMain = focused != null && _main.IsAncestorOf(focused);
        Check(focused != null && actualId == id && _main.IsActive && inMain,
            $"Keyboard.FocusedElement retained live AutomationId {id}; actual={actualId}; type={focused?.GetType().Name ?? "<null>"}; mainActive={_main.IsActive}; liveMainAncestor={inMain}");
    }
    private static void Invoke(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static void Drain() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Walk(root).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void ChineseName(DependencyObject control)
    {
        var name = AutomationProperties.GetName(control);
        Check(!string.IsNullOrWhiteSpace(name) && name.Any(c => c >= '一' && c <= '鿿'), "Chinese AutomationProperties.Name: " + AutomationProperties.GetAutomationId(control));
    }
    private static FieldInfo GetField(object target, string name) => target.GetType().GetField(name, Private)
        ?? throw new MissingFieldException(target.GetType().FullName, name);
    private static T Field<T>(object target, string name) => (T)GetField(target, name).GetValue(target)!;
    private static void Call(object target, string method)
    {
        try { (target.GetType().GetMethod(method, Private | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(method)).Invoke(target, null); }
        catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte channel) { var s = channel / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
