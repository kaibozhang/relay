using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Relay.Desktop.Services;

/// <summary>Explicit opt-in shell shortcuts. No constructor, startup registration, or automatic installation.</summary>
public static class ShortcutIntegration
{
    public const string SendToLabel = "捕捉到续页";
    public const string StartMenuLabel = "续页 Relay";
    private const string OwnerDescription = "Relay.Owner=Relay.Desktop; ShortcutIntegration.v1";
    private static readonly object Gate = new();

    public sealed record Locations(string SendTo, string StartMenu, string Desktop);

    public static Locations GetLocations() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), SendToLabel + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuLabel + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StartMenuLabel + ".lnk"));

    /// <summary>Windows appends selected paths after --sendto; the application must validate/translate them.</summary>
    public static string InstallSendTo(string exePath, string? vaultPath = null, string? folderOverride = null) =>
        Install(Location(GetLocations().SendTo, folderOverride), exePath, vaultPath, true);
    public static string InstallStartMenu(string exePath, string? vaultPath = null, string? folderOverride = null) =>
        Install(Location(GetLocations().StartMenu, folderOverride), exePath, vaultPath, false);
    public static string InstallDesktop(string exePath, string? vaultPath = null, string? folderOverride = null) =>
        Install(Location(GetLocations().Desktop, folderOverride), exePath, vaultPath, false);

    // Omitted exePath means the running native executable. Supply the original target explicitly
    // when uninstalling a shortcut for another installation. Target + exact owner marker must match.
    public static bool RemoveSendTo(string? exePath = null, string? folderOverride = null) =>
        Remove(Location(GetLocations().SendTo, folderOverride), Executable(exePath ?? Environment.ProcessPath!), true);
    public static bool RemoveStartMenu(string? exePath = null, string? folderOverride = null) =>
        Remove(Location(GetLocations().StartMenu, folderOverride), Executable(exePath ?? Environment.ProcessPath!), false);
    public static bool RemoveDesktop(string? exePath = null, string? folderOverride = null) =>
        Remove(Location(GetLocations().Desktop, folderOverride), Executable(exePath ?? Environment.ProcessPath!), false);

    private static string Location(string standard, string? folderOverride)
    {
        string folder = folderOverride is null ? Path.GetDirectoryName(standard)! : AbsolutePath(folderOverride, nameof(folderOverride));
        if (string.IsNullOrEmpty(folder)) throw new InvalidOperationException("The Windows shortcut folder is unavailable.");
        return Path.Combine(folder, Path.GetFileName(standard));
    }

    private static string AbsolutePath(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(new[] { '"', '\0', '\r', '\n' }) >= 0)
            throw new ArgumentException("An absolute path without quotes or control characters is required.", name);
        return Path.GetFullPath(path);
    }

    private static string Executable(string path)
    {
        path = AbsolutePath(path, nameof(path));
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A native .exe target is required.", nameof(path));
        return path;
    }

    private static string Quote(string value) => "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";

    private static string Install(string path, string exePath, string? vaultPath, bool sendTo)
    {
        exePath = Executable(exePath);
        if (!File.Exists(exePath)) throw new FileNotFoundException("Relay executable was not found.", exePath);
        vaultPath = vaultPath is null ? null : AbsolutePath(vaultPath, nameof(vaultPath));
        string arguments = (vaultPath is null ? "" : "--vault " + Quote(vaultPath))
            + (sendTo ? (vaultPath is null ? "" : " ") + "--capture --sendto" : "");
        lock (Gate)
        {
            bool existed = File.Exists(path);
            if (existed && !IsOwned(path, exePath, sendTo))
                throw new InvalidOperationException("A shortcut at this location is not owned by this Relay installation.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".Relay-" + Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                WithShortcut(temporary, shortcut =>
                {
                    shortcut.TargetPath = exePath;
                    shortcut.Arguments = arguments;
                    shortcut.Description = OwnerDescription;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exePath)!;
                    shortcut.IconLocation = exePath + ",0";
                    shortcut.WindowStyle = 1;
                    shortcut.Save();
                    return true;
                });
                if (!IsOwned(temporary, exePath, sendTo)) throw new IOException("The new shortcut could not be verified.");
                if (File.Exists(path) && !IsOwned(path, exePath, sendTo))
                    throw new InvalidOperationException("Shortcut ownership changed during installation.");
                // Same-directory atomic publication leaves the previous owned shortcut intact on failure.
                // Move without overwrite for a new install also protects an intervening foreign creation.
                if (existed) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return path;
            }
            finally
            {
                // Only this invocation's random staging file can be removed here.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static bool Remove(string path, string expectedExe, bool sendTo)
    {
        lock (Gate)
        {
            if (!File.Exists(path)) return false;
            if (!IsOwned(path, expectedExe, sendTo)) return false;
            File.Delete(path);
            return true;
        }
    }

    private static bool IsOwned(string path, string expectedExe, bool sendTo)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        try
        {
            return WithShortcut(path, shortcut =>
            {
                string target = shortcut.TargetPath;
                string description = shortcut.Description;
                string arguments = shortcut.Arguments;
                if (description != OwnerDescription || !string.Equals(target, expectedExe, StringComparison.OrdinalIgnoreCase)) return false;
                // Owner + exact expected executable are the ownership proof. Also require our action
                // suffix so an unrelated modified shortcut with the marker is preserved.
                return RecognizedArguments(arguments, sendTo);
            });
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { return false; }
    }

    private static bool RecognizedArguments(string arguments, bool sendTo)
    {
        string action = sendTo ? "--capture --sendto" : "";
        if (arguments == action) return true;
        string suffix = sendTo ? "\" --capture --sendto" : "\"";
        const string prefix = "--vault \"";
        if (!arguments.StartsWith(prefix, StringComparison.Ordinal) || !arguments.EndsWith(suffix, StringComparison.Ordinal)
            || arguments.Length < prefix.Length + suffix.Length) return false;
        string escaped = arguments.Substring(prefix.Length, arguments.Length - prefix.Length - suffix.Length);
        int trailing = escaped.Length - escaped.TrimEnd('\\').Length;
        if (trailing % 2 != 0) return false;
        string vault = escaped[..(escaped.Length - trailing / 2)];
        try
        {
            vault = AbsolutePath(vault, "vaultPath");
            return arguments == "--vault " + Quote(vault) + (sendTo ? " " + action : "");
        }
        catch (ArgumentException) { return false; }
    }

    private static T WithShortcut<T>(string path, Func<dynamic, T> operation)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new NotSupportedException("Windows Script Host is unavailable.");
            shell = Activator.CreateInstance(type) ?? throw new NotSupportedException("Windows Script Host could not be created.");
            shortcut = ((dynamic)shell).CreateShortcut(path);
            return operation(shortcut);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
