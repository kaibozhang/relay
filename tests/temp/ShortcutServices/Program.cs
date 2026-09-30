using System;
using System.IO;
using System.Runtime.InteropServices;
using Relay.Desktop.Services;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string exe = Environment.ProcessPath!;
        if (args.Length > 0 && args[0] == "real-sendto")
        {
            string path = ShortcutIntegration.GetLocations().SendTo;
            if (File.Exists(path)) { Console.WriteLine("SKIP: existing SendTo shortcut preserved"); return 0; }
            try
            {
                ShortcutIntegration.InstallSendTo(exe, Path.GetFullPath("tests/temp/ShortcutServices/vault 中文"));
                Check(path, exe, true);
            }
            finally { ShortcutIntegration.RemoveSendTo(exe); }
            Assert(!File.Exists(path), "real shortcut removed");
            Console.WriteLine("PASS: real SendTo install/readback/remove roundtrip"); return 0;
        }
        string root = Path.GetFullPath("tests/temp/ShortcutServices/run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string vault = Path.Combine(root, "中文 vault") + Path.DirectorySeparatorChar;
        string send = ShortcutIntegration.InstallSendTo(exe, vault, root);
        Check(send, exe, true);
        ShortcutIntegration.InstallSendTo(exe, null, root);
        Check(send, exe, true);
        Assert(ShortcutIntegration.RemoveSendTo(exe, root), "remove owned sendto");
        Assert(!ShortcutIntegration.RemoveSendTo(exe, root), "remove absent safe");
        string start = ShortcutIntegration.InstallStartMenu(exe, vault, root);
        Check(start, exe, false);
        Assert(!ShortcutIntegration.RemoveStartMenu(Path.Combine(root, "foreign.exe"), root), "wrong target preserved");
        Assert(ShortcutIntegration.RemoveStartMenu(exe, root), "remove owned start");
        string desktop = ShortcutIntegration.InstallDesktop(exe, vault, root);
        Assert(ShortcutIntegration.RemoveDesktop(exe, root), "remove owned desktop");
        // A malformed/foreign file at the exact destination must never be replaced or deleted.
        File.WriteAllText(send, "foreign shortcut data");
        bool rejected = false;
        try { ShortcutIntegration.InstallSendTo(exe, vault, root); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "foreign overwrite refused");
        Assert(!ShortcutIntegration.RemoveSendTo(exe, root), "foreign deletion refused");
        Assert(File.ReadAllText(send) == "foreign shortcut data", "foreign bytes unchanged");
        // This file was created by this harness, not the production uninstaller.
        File.Delete(send);
        Directory.Delete(root);
        Console.WriteLine("PASS: temp SendTo/StartMenu/Desktop install/readback/update/remove, wrong target and foreign shortcut safeguards");
        return 0;
    }
    static void Check(string path, string exe, bool sendTo)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            Assert((string)((dynamic)shortcut).TargetPath == exe, "target roundtrip");
            string arguments = ((dynamic)shortcut).Arguments;
            Assert(sendTo ? arguments.EndsWith("--capture --sendto") : !arguments.Contains("--capture"), "action roundtrip");
            Console.WriteLine(path + " => " + arguments);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
    static void Assert(bool result, string message) { if (!result) throw new Exception("FAIL: " + message); }
}
