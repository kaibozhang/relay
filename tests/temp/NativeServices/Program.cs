using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Relay.Desktop.Services;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
        {
            using var child = new SingleInstance();
            bool result = child.Acquire(args[1], new[] { "--capture", "--resource", "D:\\中文 folder\\draft.md" }, _ => {});
            Console.WriteLine("child owner=" + result);
            return result ? 2 : 0;
        }
        if (args.Length > 0 && args[0] == "registry") return TestRegistry();
        if (args.Length > 0 && args[0] == "native") return TestNative();
        TestRollbackJournal();
        string key = "native-service-test-" + Guid.NewGuid();
        int delivered = 0;
        using var owner = new SingleInstance();
        Assert(owner.Acquire(key, Array.Empty<string>(), values =>
        {
            Assert(values[0] == "--capture", "action");
            Interlocked.Increment(ref delivered);
        }), "first owner");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("child"); start.ArgumentList.Add(key);
        using (var process = Process.Start(start)!)
        {
            Assert(process.WaitForExit(10000), "child completed");
            Assert(process.ExitCode == 0, "cross-process delivered");
        }
        Assert(delivered == 1, "callback exactly once");
        using (var other = new SingleInstance())
            Assert(other.Acquire(key + "-other", Array.Empty<string>(), _ => {}), "vault isolation");
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string pipeName = "Relay.Desktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\n" + key)));
        // Oversized frame and forbidden action must not reach application callbacks or kill listener.
        SendRaw(pipeName, int.MaxValue, null);
        byte[] bad = JsonSerializer.SerializeToUtf8Bytes(new[] { "--quit" });
        SendRaw(pipeName, bad.Length, bad);
        using (var next = new SingleInstance())
            Assert(!next.Acquire(key, new[] { "--capture", "--resource", new string('中', 32768) }, _ => {}), "max argument delivery");
        Assert(delivered == 2, "malformed clients not delivered");
        foreach (string[] invalid in new[] {
            new[] { "--quit" }, new[] { "--resource" }, new[] { "--capture", "--resume" },
            new[] { "--capture", "--resource", new string('x',32769) }, new[] { "--resource", "x\0y" }, new string[33]
        })
        {
            bool rejected = false;
            try { SingleInstance.ValidateArguments(invalid); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "invalid arguments rejected");
        }
        Task.Run(() => owner.Dispose()).GetAwaiter().GetResult();
        using (var nextOwner = new SingleInstance())
            Assert(nextOwner.Acquire(key, Array.Empty<string>(), _ => {}), "ownership reacquired after cross-thread dispose");
        // Rapid disposal exercises listener task scheduling and cancellation ownership.
        for (int i = 0; i < 50; i++)
        {
            using var quick = new SingleInstance();
            Assert(quick.Acquire(key + "-quick", Array.Empty<string>(), _ => {}), "rapid lifetime");
        }
        Console.WriteLine("PASS: cross-process IPC, vault isolation, malformed frames, allowlist, max length, cross-thread disposal, rapid lifetime");
        return 0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    static int TestNative()
    {
        // No Application object: deliberately avoid persisting a test executable JumpList.
        var window = new System.Windows.Window();
        int capture = 0, resume = 0;
        using var integration = new WindowsIntegration(window, () => capture++, () => resume++, () => {}, () => {});
        Assert(integration.UpdateHotkeys("alt+ctrl+shift+J", "Ctrl+Alt+Shift+K"), "pre-init hotkey configure");
        Assert(integration.CaptureHotkeyText == "Ctrl+Alt+Shift+J", "normalized hotkey text");
        integration.Initialize(); integration.Initialize();
        Console.WriteLine(integration.Status);
        Assert(integration.HasTray, "tray availability");
        Assert(window.TaskbarItemInfo.ThumbButtonInfos.Count == 2, "two taskbar buttons");
        integration.SetTaskbarState(new string('中', 100), 2, false);
        Assert(window.TaskbarItemInfo.ProgressValue == 1, "progress clamp");
        integration.SetTaskbarState(null, double.NaN, false);
        Assert(window.TaskbarItemInfo.ProgressState == System.Windows.Shell.TaskbarItemProgressState.None, "nan clears");
        integration.SetTaskbarState("paused", .3, true);
        Assert(window.TaskbarItemInfo.ProgressState == System.Windows.Shell.TaskbarItemProgressState.Paused, "paused state");
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        PostMessage(handle, 0x312, new IntPtr(0x5241), IntPtr.Zero);
        PostMessage(handle, 0x312, new IntPtr(0x5242), IntPtr.Zero);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert(capture == (integration.CaptureHotkeyRegistered ? 1 : 0), "capture hotkey dispatch");
        Assert(resume == (integration.ResumeHotkeyRegistered ? 1 : 0), "resume hotkey dispatch");
        Assert(integration.CaptureHotkeyRegistered && integration.ResumeHotkeyRegistered, "configured initial registration");
        foreach (string invalid in new[] { "Win+J", "Shift+J", "Ctrl+Ctrl+J", "Ctrl+F1", "Ctrl++J", "Ctrl+1" })
            Assert(!integration.UpdateHotkeys(invalid, "Ctrl+Alt+Shift+K"), "reject " + invalid);
        Assert(!integration.UpdateHotkeys("Ctrl+Alt+J", "Alt+Ctrl+J"), "reject identical normalized bindings");
        var blocker = new System.Windows.Window();
        IntPtr blockedHandle = new System.Windows.Interop.WindowInteropHelper(blocker).EnsureHandle();
        Assert(RegisterHotKey(blockedHandle, 987, 0x4007, (uint)'L'), "helper conflict registration");
        try
        {
            Assert(!integration.UpdateHotkeys("Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+M"), "conflict update rejected");
            Assert(integration.CaptureHotkeyText == "Ctrl+Alt+Shift+J" && integration.ResumeHotkeyText == "Ctrl+Alt+Shift+K", "old text restored");
            Assert(integration.CaptureHotkeyRegistered && integration.ResumeHotkeyRegistered, "old bindings restored");
        }
        finally { UnregisterHotKey(blockedHandle, 987); blocker.Close(); }
        Assert(integration.UpdateHotkeys("Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+M"), "change succeeds after blocker release");
        Assert(window.TaskbarItemInfo.ThumbButtonInfos[0].Description.Contains("Ctrl+Alt+Shift+L"), "thumb description current");
        Assert(!integration.Status.Contains("Ctrl+Alt+Shift+J"), "status not stale");
        Assert(integration.UpdateHotkeys("Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+L"), "swap bindings");
        bool wasCapture = integration.CaptureHotkeyRegistered, wasResume = integration.ResumeHotkeyRegistered;
        integration.Dispose(); integration.Dispose();
        Assert(!integration.HasTray, "tray no longer available");
        Assert(window.TaskbarItemInfo is null, "taskbar ownership cleanup");
        using (var again = new WindowsIntegration(window, () => {}, () => {}, () => {}, () => {}))
        {
            Assert(again.UpdateHotkeys("Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+L"), "reconfigure released bindings");
            again.Initialize();
            Assert(!wasCapture || again.CaptureHotkeyRegistered, "capture unregistered");
            Assert(!wasResume || again.ResumeHotkeyRegistered, "resume unregistered");
        }
        window.Close();
        var command = typeof(WindowsIntegration).GetMethod("ExplorerCommand", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string exe = @"C:\Program Files\续页\Relay.exe";
        Assert((string)command.Invoke(null, new object?[] { exe, 0, null })! == "\"" + exe + "\" --capture --resource \"%1\"", "file verb quoted");
        Assert((string)command.Invoke(null, new object?[] { exe, 2, null })! == "\"" + exe + "\" --capture --resource \"%V\"", "background verb quoted");
        string vault = @"D:\中文 vault\";
        string vaultCommand = (string)command.Invoke(null, new object?[] { exe, 0, vault })!;
        Assert(vaultCommand == "\"" + exe + "\" --vault \"" + vault + "\\\" --capture --resource \"%1\"", "vault trailing slash quoted");
        var vaultArgsMethod = typeof(WindowsIntegration).GetMethod("VaultArguments", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string vaultArgs = (string)vaultArgsMethod.Invoke(null, new object?[] { vault })!;
        Assert(vaultArgs + "--resume" == "--vault \"" + vault + "\\\" --resume", "jump vault argument quoted");
        Console.WriteLine("PASS: native hotkey messages, disposal/re-registration, HasTray, taskbar progress, quoted vault/Explorer/JumpList command generation. No registry writes.");
        return 0;
    }
    static void TestRollbackJournal()
    {
        var transaction = typeof(WindowsIntegration).GetMethod("RunRegistryTransaction", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        int value = 1;
        var order = new System.Collections.Generic.List<int>();
        Action<System.Collections.Generic.List<Action>> work = undo =>
        {
            undo.Add(() => { value = 1; order.Add(1); }); value = 2;
            undo.Add(() => { value = 2; order.Add(2); }); value = 3;
            throw new IOException("injected installation failure");
        };
        try { transaction.Invoke(null, new object[] { work }); throw new Exception("Expected injected failure"); }
        catch (System.Reflection.TargetInvocationException error)
        { Assert(error.InnerException is IOException, "original installation failure retained"); }
        Assert(value == 1 && order.Count == 2 && order[0] == 2 && order[1] == 1, "reverse rollback restores original");
        work = undo =>
        {
            undo.Add(() => value = 1);
            undo.Add(() => throw new UnauthorizedAccessException("injected rollback denial"));
            value = 3;
            throw new IOException("injected installation failure");
        };
        try { transaction.Invoke(null, new object[] { work }); throw new Exception("Expected injected failure"); }
        catch (System.Reflection.TargetInvocationException error)
        {
            Assert(error.InnerException is IOException, "original error retained after rollback denial");
            Assert(error.InnerException!.Data["Relay.RegistryRollbackErrors"] is AggregateException, "rollback denial surfaced");
        }
        Assert(value == 1, "remaining undos continue after rollback failure");
        Console.WriteLine("PASS: injected transaction failure rollback order, restoration, rollback-error reporting (no registry writes)");
    }
    static int TestRegistry()
    {
        string[] keys = {
            @"Software\Classes\*\shell\Relay.Capture",
            @"Software\Classes\Directory\shell\Relay.Capture",
            @"Software\Classes\Directory\Background\shell\Relay.Capture"
        };
        // Snapshot absence first; skip for ANY pre-existing entry, owned or foreign.
        foreach (string path in keys)
        {
            using var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            if (existing is not null) { Console.WriteLine("SKIP registry test: pre-existing key " + path); return 0; }
        }
        string exe = Environment.ProcessPath!;
        string vault = Path.GetFullPath(@"tests\temp\NativeServices\测试 vault\");
        try
        {
            WindowsIntegration.InstallExplorerMenu(exe, vault);
            Assert(WindowsIntegration.ExplorerMenuInstalled, "vault registry installed");
            for (int i = 0; i < keys.Length; i++)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keys[i])!;
                Assert((string)key.GetValue("Relay.Owner")! == "Relay.Desktop", "owner marker");
                Assert((string)key.GetValue("Relay.Vault")! == vault, "vault marker");
                Assert((string)key.GetValue("MultiSelectModel")! == "Single", "single selection");
                using var command = key.OpenSubKey("command")!;
                string actual = (string)command.GetValue("")!;
                string expected = "\"" + exe + "\" --vault \"" + vault + "\\\" --capture --resource \"" + (i == 2 ? "%V" : "%1") + "\"";
                Assert(actual == expected, "vault command quoting");
                Console.WriteLine(actual);
            }
            WindowsIntegration.RemoveExplorerMenu();
            Assert(!WindowsIntegration.ExplorerMenuInstalled, "vault registry uninstalled");
            WindowsIntegration.InstallExplorerMenu(exe);
            Assert(WindowsIntegration.ExplorerMenuInstalled, "legacy no-vault registry installed");
        }
        finally { WindowsIntegration.RemoveExplorerMenu(); }
        foreach (string path in keys)
        {
            using var remaining = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            Assert(remaining is null, "registry restored to absent snapshot");
        }
        Console.WriteLine("PASS: explicit reversible Explorer registration, all markers/quoted vault commands, uninstall, no pre-existing keys modified");
        return 0;
    }
    static void SendRaw(string name, int length, byte[]? payload)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        pipe.Connect(5000);
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        pipe.Write(header); if (payload is not null) pipe.Write(payload); pipe.Flush();
        Assert(pipe.ReadByte() == -1, "rejected frame closes connection");
    }
    static void Assert(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); }
}
