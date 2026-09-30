using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Relay.Desktop.Services;

/// <summary>Optional native affordances. Construct, initialize and dispose on the window dispatcher.</summary>
public sealed class WindowsIntegration : IDisposable
{
    public const string AppId = "Relay.Desktop";
    private const int CaptureId = 0x5241;
    private const int ResumeId = 0x5242;
    private const int WmHotkey = 0x0312;
    private const uint Modifiers = 0x0001 | 0x0002 | 0x4000; // Alt + Control + NoRepeat
    private const string OwnerValue = "Relay.Owner";
    private const string ExecutableValue = "Relay.Executable";
    private const string VaultValue = "Relay.Vault";
    private static readonly string[] ExplorerKeys =
    {
        @"Software\Classes\*\shell\Relay.Capture",
        @"Software\Classes\Directory\shell\Relay.Capture",
        @"Software\Classes\Directory\Background\shell\Relay.Capture"
    };
    private readonly Window _window;
    private readonly Action _capture;
    private readonly Action _resume;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly string? _vaultPath;
    private readonly List<string> _status = new();
    private HwndSource? _source;
    private IntPtr _handle;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private TaskbarItemInfo? _taskbar;
    private TaskbarItemInfo? _previousTaskbar;
    private JumpList? _jumpList;
    private JumpList? _previousJumpList;
    private bool _initialized;
    private bool _disposed;

    public WindowsIntegration(Window window, Action capture, Action resume, Action show, Action quit, string? vaultPath = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _resume = resume ?? throw new ArgumentNullException(nameof(resume));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _quit = quit ?? throw new ArgumentNullException(nameof(quit));
        _vaultPath = NormalizeVault(vaultPath);
    }

    public bool HasTray => !_disposed && _tray is { Visible: true };
    public bool CaptureHotkeyRegistered { get; private set; }
    public bool ResumeHotkeyRegistered { get; private set; }
    public string Status => !_initialized ? (_disposed ? "Windows 集成已关闭" : "Windows 集成尚未初始化") : string.Join("；", _status);

    public void Initialize()
    {
        _window.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        _initialized = true;
        // Must precede EnsureHandle so the first taskbar window gets the correct identity.
        TryFeature("应用标识", () => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId)));
        TryFeature("全局快捷键", InitializeHotkeys);
        TryFeature("托盘", InitializeTray);
        TryFeature("任务栏", InitializeTaskbar);
        TryFeature("跳转列表", InitializeJumpList);
        _window.Closed += WindowClosed;
    }

    private void TryFeature(string name, Action initialize)
    {
        try { initialize(); }
        catch (Exception error) when (error is Win32Exception or COMException or InvalidOperationException
            or ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            _status.Add(name + "不可用（" + error.GetType().Name + "）");
        }
    }

    private void InitializeHotkeys()
    {
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("Window source is unavailable.");
        _source.AddHook(WindowHook);
        CaptureHotkeyRegistered = RegisterHotKey(_handle, CaptureId, Modifiers, 0x20);
        int captureError = CaptureHotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
        ResumeHotkeyRegistered = RegisterHotKey(_handle, ResumeId, Modifiers, 0x52);
        int resumeError = ResumeHotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
        _status.Add(CaptureHotkeyRegistered ? "Ctrl+Alt+Space 已启用" : $"Ctrl+Alt+Space 注册失败（{captureError}）");
        _status.Add(ResumeHotkeyRegistered ? "Ctrl+Alt+R 已启用" : $"Ctrl+Alt+R 注册失败（{resumeError}）");
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && message == WmHotkey)
        {
            if (wParam.ToInt64() == CaptureId && CaptureHotkeyRegistered) { handled = true; Dispatch(_capture); }
            else if (wParam.ToInt64() == ResumeId && ResumeHotkeyRegistered) { handled = true; Dispatch(_resume); }
        }
        return IntPtr.Zero;
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    private void InitializeTray()
    {
        // Clone the fallback: SystemIcons.Application is shared and must never be disposed by us.
        _icon = (Icon)SystemIcons.Application.Clone();
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) { _icon.Dispose(); _icon = extracted; }
            }
            catch (Exception error) when (error is ArgumentException or IOException or Win32Exception or UnauthorizedAccessException) { }
        }
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("捕捉续做卡", null, (_, _) => Dispatch(_capture));
        _menu.Items.Add("继续上次工作", null, (_, _) => Dispatch(_resume));
        _menu.Items.Add("打开续页", null, (_, _) => Dispatch(_show));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Dispatch(_quit));
        _tray = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = Clip("续页 Relay · 捕捉此刻，接续工作", 63),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _tray.DoubleClick += TrayDoubleClick;
        _tray.BalloonTipClicked += TrayDoubleClick;
        _status.Add("托盘已启用");
    }

    private void TrayDoubleClick(object? sender, EventArgs args) => Dispatch(_show);

    private void InitializeTaskbar()
    {
        _previousTaskbar = _window.TaskbarItemInfo;
        _taskbar = new TaskbarItemInfo { Description = "续页 Relay" };
        var captureButton = new ThumbButtonInfo
        {
            Description = "捕捉续做卡 (Ctrl+Alt+Space)",
            ImageSource = DrawIcon("M 3,2 L 11,2 14,5 14,14 3,14 Z M 6,7 L 11,7 M 6,10 L 11,10", false)
        };
        captureButton.Click += CaptureThumbClick;
        var resumeButton = new ThumbButtonInfo
        {
            Description = "继续上次工作 (Ctrl+Alt+R)",
            ImageSource = DrawIcon("M 4,2 L 13,8 4,14 Z", true)
        };
        resumeButton.Click += ResumeThumbClick;
        _taskbar.ThumbButtonInfos.Add(captureButton);
        _taskbar.ThumbButtonInfos.Add(resumeButton);
        _window.TaskbarItemInfo = _taskbar;
        _status.Add("任务栏快捷操作已启用");
    }

    private void CaptureThumbClick(object? sender, EventArgs args) => Dispatch(_capture);
    private void ResumeThumbClick(object? sender, EventArgs args) => Dispatch(_resume);

    private static ImageSource DrawIcon(string path, bool filled)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 137, 127));
        var drawing = new GeometryDrawing(filled ? brush : null, new System.Windows.Media.Pen(brush, 1.6), Geometry.Parse(path));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private void InitializeJumpList()
    {
        WpfApplication? application = WpfApplication.Current;
        string? exe = Environment.ProcessPath;
        if (application is null || string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            _status.Add("跳转列表需要原生应用可执行文件");
            return;
        }
        _previousJumpList = JumpList.GetJumpList(application);
        _jumpList = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        _jumpList.JumpItems.Add(CreateJumpTask(exe, "捕捉续做卡", VaultArguments(_vaultPath) + "--capture", "把当前工作留给下一次的自己"));
        _jumpList.JumpItems.Add(CreateJumpTask(exe, "继续上次工作", VaultArguments(_vaultPath) + "--resume", "回到最近的工作续点"));
        JumpList.SetJumpList(application, _jumpList);
        _jumpList.Apply();
        _status.Add("跳转列表已请求（由 Windows 管理）");
    }

    private static JumpTask CreateJumpTask(string exe, string title, string arguments, string description) => new()
    {
        Title = title,
        Description = description,
        ApplicationPath = exe,
        Arguments = arguments,
        WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        IconResourcePath = exe,
        IconResourceIndex = 0
    };

    public void ShowNotification(string title, string text)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed || _tray is null) return;
        _tray.BalloonTipTitle = Clip(title, 63);
        _tray.BalloonTipText = Clip(text, 255);
        _tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>Progress is clamped to 0..1; zero/non-finite clears it unless paused.</summary>
    public void SetTaskbarState(string? title, double progress, bool paused)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        string description = string.IsNullOrWhiteSpace(title) ? "续页 Relay" : title;
        if (_tray is not null) _tray.Text = Clip(description, 63);
        if (_taskbar is null) return;
        double value = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        _taskbar.Description = Clip(description, 260);
        _taskbar.ProgressValue = value;
        _taskbar.ProgressState = paused ? TaskbarItemProgressState.Paused
            : value > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;
    }

    private static string Clip(string? value, int length)
    {
        value = (value ?? "").Replace('\0', ' ');
        if (value.Length <= length) return value;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    /// <summary>True only when all three exact Relay-owned commands are present.</summary>
    public static bool ExplorerMenuInstalled
    {
        get
        {
            try
            {
                for (int i = 0; i < ExplorerKeys.Length; i++)
                {
                    using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExplorerKeys[i]);
                    if (key is null || !IsOwnedMenu(key, i)) return false;
                }
                return true;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException) { return false; }
        }
    }

    /// <summary>
    /// Explicit opt-in only. Adds verbs, never a default opener or startup item.
    /// On Windows 11 these classic verbs normally appear under “Show more options”.
    /// </summary>
    public static void InstallExplorerMenu(string exePath, string? vaultPath = null)
    {
        vaultPath = NormalizeVault(vaultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!Path.IsPathFullyQualified(exePath) || exePath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("An absolute executable path without quotes or control characters is required.", nameof(exePath));
        exePath = Path.GetFullPath(exePath);
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath))
            throw new ArgumentException("The application executable does not exist.", nameof(exePath));
        // Preflight all keys before the first write. Recheck ownership on each opened handle too.
        for (int i = 0; i < ExplorerKeys.Length; i++)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(ExplorerKeys[i]);
            if (existing is not null && !IsOwnedMenu(existing, i))
                throw new InvalidOperationException("An Explorer entry with this name is not owned by Relay.");
        }
        RunRegistryTransaction(undo =>
        {
            for (int i = 0; i < ExplorerKeys.Length; i++)
            {
                using RegistryKey key = CreateTrackedKey(ExplorerKeys[i], undo, out bool created);
                if (!created && !IsOwnedMenu(key, i))
                    throw new InvalidOperationException("Explorer entry ownership changed during installation.");
                SetTrackedValue(key, ExplorerKeys[i], OwnerValue, AppId, undo);
                SetTrackedValue(key, ExplorerKeys[i], ExecutableValue, exePath, undo);
                SetTrackedValue(key, ExplorerKeys[i], VaultValue, vaultPath, undo);
                SetTrackedValue(key, ExplorerKeys[i], "", "捕捉到续页", undo);
                SetTrackedValue(key, ExplorerKeys[i], "Icon", "\"" + exePath + "\",0", undo);
                SetTrackedValue(key, ExplorerKeys[i], "MultiSelectModel", "Single", undo);
                string commandPath = ExplorerKeys[i] + @"\command";
                using RegistryKey command = CreateTrackedKey(commandPath, undo, out _);
                SetTrackedValue(command, commandPath, "", ExplorerCommand(exePath, i, vaultPath), undo);
            }
        });
        NotifyShellChanged();
    }

    // Undo journal is value-granular: preserve original kinds/data, and never overwrite a
    // value another writer changed after us. Rollback failures are attached to the original
    // exception rather than hidden; a permissions failure cannot promise atomic restoration.
    private static void RunRegistryTransaction(Action<List<Action>> work)
    {
        var undo = new List<Action>();
        try { work(undo); }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i](); }
                catch (Exception rollbackError) { failures.Add(rollbackError); }
            }
            if (failures.Count > 0) error.Data["Relay.RegistryRollbackErrors"] = new AggregateException(failures);
            NotifyShellChanged();
            throw;
        }
    }

    private static RegistryKey CreateTrackedKey(string path, List<Action> undo, out bool created)
    {
        // Disposition proves creation atomically; an absence check followed by CreateSubKey
        // cannot distinguish a racing foreign key and is not sufficient provenance for removal.
        int result = RegCreateKeyEx(Registry.CurrentUser.Handle, path, 0, null, 0, 0x2001F,
            IntPtr.Zero, out Microsoft.Win32.SafeHandles.SafeRegistryHandle handle, out int disposition);
        if (result != 0)
        {
            handle.Dispose();
            if (result == 5) throw new UnauthorizedAccessException("Windows denied Explorer menu registry access.");
            throw new Win32Exception(result);
        }
        created = disposition == 1;
        RegistryKey key = RegistryKey.FromHandle(handle);
        if (created)
        {
            undo.Add(() =>
            {
                using RegistryKey? remaining = Registry.CurrentUser.OpenSubKey(path);
                if (remaining is null) return;
                // Value undos run first; foreign additions prevent deletion. Never recurse.
                if (remaining.ValueCount != 0 || remaining.SubKeyCount != 0)
                    throw new InvalidOperationException("Rollback retained a newly created key because it contains unexpected data.");
                Registry.CurrentUser.DeleteSubKey(path, false);
            });
        }
        return key;
    }

    private static void SetTrackedValue(RegistryKey key, string path, string name, string? value, List<Action> undo)
    {
        bool existed = key.GetValueNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        object? before = existed ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        RegistryValueKind kind = existed ? key.GetValueKind(name) : RegistryValueKind.String;
        undo.Add(() =>
        {
            using RegistryKey? current = Registry.CurrentUser.OpenSubKey(path, true);
            if (current is null) throw new InvalidOperationException("Rollback key is no longer present.");
            bool present = current.GetValueNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            object? actual = present ? current.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            // A failed SetValue may leave the previous value intact: that is already restored.
            if (present == existed && (!present || (current.GetValueKind(name) == kind && RegistryValuesEqual(actual, before)))) return;
            bool stillOurs = value is null ? !present
                : present && current.GetValueKind(name) == RegistryValueKind.String && Equals(actual, value);
            if (!stillOurs) throw new InvalidOperationException("Rollback preserved a concurrently modified registry value.");
            if (existed) current.SetValue(name, before!, kind);
            else current.DeleteValue(name, false);
        });
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static bool RegistryValuesEqual(object? left, object? right) =>
        left is Array a && right is Array b
            ? System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(a, b)
            : Equals(left, right);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string subKey,
        int reserved, string? keyClass, int options, int access, IntPtr security,
        out Microsoft.Win32.SafeHandles.SafeRegistryHandle result, out int disposition);

    /// <summary>Removes only exact, marked, unmodified Relay entries; foreign entries are left untouched.</summary>
    public static void RemoveExplorerMenu()
    {
        for (int i = 0; i < ExplorerKeys.Length; i++)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExplorerKeys[i]);
            if (key is null || !IsOwnedMenu(key, i)) continue;
            // No recursive deletion: unexpected nested keys or values are an ownership conflict.
            if (key.GetSubKeyNames().Any(name => !name.Equals("command", StringComparison.OrdinalIgnoreCase))) continue;
            if (key.GetValueNames().Any(name => name is not ("" or OwnerValue or ExecutableValue or VaultValue or "Icon" or "MultiSelectModel"))) continue;
            using (RegistryKey? command = key.OpenSubKey("command"))
            {
                if (command is null || command.SubKeyCount != 0 || command.GetValueNames().Any(name => name != "")) continue;
            }
            Registry.CurrentUser.DeleteSubKey(ExplorerKeys[i] + @"\command", false);
            Registry.CurrentUser.DeleteSubKey(ExplorerKeys[i], false);
        }
        NotifyShellChanged();
    }

    private static bool IsOwnedMenu(RegistryKey key, int index)
    {
        if (!string.Equals(key.GetValue(OwnerValue) as string, AppId, StringComparison.Ordinal)) return false;
        string? exe = key.GetValue(ExecutableValue) as string;
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe) || exe.Contains('"')) return false;
        string? vault = key.GetValue(VaultValue) as string;
        try { vault = NormalizeVault(vault); }
        catch (ArgumentException) { return false; }
        using RegistryKey? command = key.OpenSubKey("command");
        return string.Equals(command?.GetValue("") as string, ExplorerCommand(exe, index, vault), StringComparison.Ordinal);
    }

    private static string ExplorerCommand(string exe, int index, string? vaultPath = null) =>
        QuoteArgument(exe) + " " + VaultArguments(vaultPath) + "--capture --resource \"" + (index == 2 ? "%V" : "%1") + "\"";

    private static string? NormalizeVault(string? vaultPath)
    {
        if (vaultPath is null) return null;
        if (string.IsNullOrWhiteSpace(vaultPath) || !Path.IsPathFullyQualified(vaultPath)
            || vaultPath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("An absolute vault directory without quotes or control characters is required.", nameof(vaultPath));
        return Path.GetFullPath(vaultPath);
    }

    private static string VaultArguments(string? vaultPath) => vaultPath is null ? "" : "--vault " + QuoteArgument(vaultPath) + " ";

    // Windows command-line quoting doubles trailing backslashes before the closing quote.
    // Paths are validated to exclude embedded quotes before calling this helper.
    private static string QuoteArgument(string value)
    {
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    private static void NotifyShellChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _disposed = true;
        _window.Closed -= WindowClosed;
        if (_handle != IntPtr.Zero)
        {
            if (CaptureHotkeyRegistered) UnregisterHotKey(_handle, CaptureId);
            if (ResumeHotkeyRegistered) UnregisterHotKey(_handle, ResumeId);
        }
        CaptureHotkeyRegistered = ResumeHotkeyRegistered = false;
        _source?.RemoveHook(WindowHook);
        _source = null;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.DoubleClick -= TrayDoubleClick;
            _tray.BalloonTipClicked -= TrayDoubleClick;
            _tray.Dispose();
            _tray = null;
        }
        _menu?.Dispose();
        _icon?.Dispose();
        if (_taskbar is not null)
        {
            foreach (ThumbButtonInfo button in _taskbar.ThumbButtonInfos)
            {
                button.Click -= CaptureThumbClick;
                button.Click -= ResumeThumbClick;
            }
            if (ReferenceEquals(_window.TaskbarItemInfo, _taskbar)) _window.TaskbarItemInfo = _previousTaskbar;
        }
        WpfApplication? application = WpfApplication.Current;
        if (application is not null && _jumpList is not null && ReferenceEquals(JumpList.GetJumpList(application), _jumpList))
            JumpList.SetJumpList(application, _previousJumpList);
        _initialized = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
