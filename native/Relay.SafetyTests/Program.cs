using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Relay.Desktop.Services;

// Pure net10 console: no WPF/app startup, network mappings, timers, watchers, or soak work.
// All mutations stay below a unique workspace fixture root and are removed after each run.
internal static class Program
{
    private const string Original = "original fixture — 中文";
    private static readonly List<(string Name, Action<Fixture> Run)> Tests = [];
    private static void Add(string name, Action<Fixture> run) => Tests.Add((name, run));
    public static int Main(string[] args)
    {
        if (args.Length != 0) return args.Length == 3 && args[0] == "--probe-lock" ? ProbeLock(args[1], args[2]) : 64;
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("TOTAL pass=0 skip=1 fail=0 (Windows required)"); return 0; }
        var root = Path.Combine(Workspace(), "native", "Relay.SafetyTests", ".fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Fixture root: " + root);
        Resources(); Exports(); Locks();
        int pass = 0, skip = 0, fail = 0, index = 0;
        foreach (var (name, run) in Tests)
        {
            Fixture? fixture = null;
            try { fixture = new Fixture(Path.Combine(root, (++index).ToString("D3"))); run(fixture); pass++; Console.WriteLine("PASS " + name); }
            catch (Skipped ex) { skip++; Console.WriteLine("SKIP " + name + ": " + ex.Message); }
            catch (Exception ex) { fail++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { fixture?.Dispose(); } catch (Exception ex) { fail++; Console.WriteLine("FAIL cleanup: " + ex.Message); } }
        }
        try { Directory.Delete(root, false); } catch (Exception ex) { fail++; Console.WriteLine("FAIL root cleanup: " + ex.Message); }
        Console.WriteLine($"TOTAL pass={pass} skip={skip} fail={fail}");
        return fail == 0 ? 0 : 1;
    }
    private static void Resources()
    {
        Add("Resource valid Unicode file", f => Local(f.File("资料 🧪.txt"), true));
        Add("Resource valid directory", f => Local(f.Dir("local"), true));
        Add("Resource missing leaf rejected", f => Local(f.PathOf("missing"), false));
        Add("Resource optional missing leaf accepted", f => Local(f.PathOf("missing"), true, true));
        Add("Resource missing ancestor rejected", f => Local(f.PathOf("missing", "leaf"), false, true));
        Add("Resource file ancestor rejected", f => Local(Path.Combine(f.File("plain"), "leaf"), false, true));
        foreach (var value in new[] { "", " ", "relative.txt", @".\relative", @"..\relative", "C:relative", @"\rooted", @"\\relay-safety.invalid\share\never-probed", "//relay-safety.invalid/share/never-probed", @"\\?\C:\never-probed", @"\\.\C:\never-probed", @"\??\C:\never-probed" })
            Add("Resource lexical rejection " + value, _ => Local(value, false, true));
        Add("Resource ADS rejected", f => Local(f.File("plain") + ":secret", false, true));
        foreach (var code in new[] { 0, 9, 10, 13, 31, 127 })
            Add($"Resource control U+{code:X4}", f => Local(f.PathOf("bad" + (char)code + ".txt"), false, true));
        foreach (var leaf in new[] { "CON", "NUL.txt", "AUX", "PRN", "COM1.txt", "LPT1", "wild*.txt", "wild?.txt", "bad|name", "bad<name", "bad\"name" })
            Add("Resource invalid leaf fail-safe " + leaf, f => Local(f.PathOf(leaf), false, true));
        Add("Resource actual drive is fixed/removable", f => Check(new DriveInfo(Path.GetPathRoot(f.Root)!).DriveType is DriveType.Fixed or DriveType.Removable, "Nonlocal fixture drive"));
        Add("Resource mapped network drive", _ => throw new Skipped("No network access or drive mapping created; fixture unavailable."));
        foreach (var junction in new[] { false, true })
        {
            var kind = junction ? "junction" : "symlink";
            Add("Resource rejects " + kind + " leaf", f => Local(f.DirAlias("alias", f.Dir("real"), junction), false));
            Add("Resource rejects " + kind + " before missing descendant", f =>
            {
                var alias = f.DirAlias("alias", f.Dir("real"), junction);
                Local(Path.Combine(alias, "absent", "leaf"), false, true, true);
            });
            Add("Resource rejects " + kind + " existing descendant", f =>
            {
                var real = f.Dir("real"); System.IO.File.WriteAllText(Path.Combine(real, "leaf"), Original);
                Local(Path.Combine(f.DirAlias("alias", real, junction), "leaf"), false, false, true);
            });
        }
        Add("Resource file symlink rejected", f => Local(f.FileAlias("alias", f.File("real"), false), false, false, true));
    }
    private static void Exports()
    {
        Add("Export Unicode exact UTF8 without BOM", f =>
        {
            var target = f.PathOf("导出 🧪.txt"); const string text = "中文 café 🧪\r\nsecond line\n";
            AtomicExport.WriteText(target, text, f.Vault);
            Check(System.IO.File.ReadAllBytes(target).SequenceEqual(new UTF8Encoding(false, true).GetBytes(text)), "UTF8 mismatch"); f.NoTemps();
        });
        Add("Export existing replaced atomically (old handle snapshot)", f =>
        {
            var target = f.File("existing");
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            AtomicExport.WriteText(target, "replacement 中文", f.Vault);
            using var reader = new StreamReader(held, Encoding.UTF8);
            Check(reader.ReadToEnd() == Original, "Original inode truncated or modified in place");
            Check(System.IO.File.ReadAllText(target) == "replacement 中文", "Replacement missing"); f.NoTemps();
        });
        Add("Export empty content", f => { var target = f.File("empty"); AtomicExport.WriteText(target, "", f.Vault); Check(new FileInfo(target).Length == 0, "Not empty"); f.NoTemps(); });
        foreach (var name in new[] { "vault.json", "VAULT.JSON", "Vault.Json.BAK", "vault.json.bak", "vault.json.corrupt", "vault.json.corrupt.20260601", "vault.json.tmp", "focus.json", "FOCUS.JSON", "focus.json.bak", "Focus.Json.Corrupt.20260601", "focus.json.tmp", ".relay.lock", ".RELAY.LOCK" })
            Add("Export protects " + name, f => { var target = Path.Combine(f.Vault, name); System.IO.File.WriteAllText(target, Original); Protected(f, target, target, f.Vault); });
        Add("Export protects missing reserved name", f =>
        {
            var target = Path.Combine(f.Vault, "vault.json.bak"); Reject(() => AtomicExport.WriteText(target, "bad", f.Vault));
            Check(!System.IO.File.Exists(target), "Reserved target created"); f.NoTemps();
        });
        Add("Export protected normalized directory and case", f =>
        {
            var target = Path.Combine(f.Vault, "vault.json"); System.IO.File.WriteAllText(target, Original);
            Protected(f, target.ToUpperInvariant(), target, Path.Combine(f.Vault, ".").ToUpperInvariant() + Path.DirectorySeparatorChar);
        });
        // Intentional conservative policy: reserve backing-data names in every directory,
        // even when lexical directory comparisons could be bypassed by short paths or subst.
        foreach (var name in new[] { "vault.json", "VAULT.JSON.BAK", "focus.json", "Focus.Json.Corrupt", ".relay.lock", ".RELAY.LOCK" })
            Add("Export reserved basename outside vault rejected " + name, f =>
            {
                var target = f.File(name);
                Protected(f, target, target, f.Vault);
            });
        Add("Export missing lock basename outside vault rejected", f =>
        {
            var target = f.PathOf(".relay.lock");
            Reject(() => AtomicExport.WriteText(target, "bad", f.Vault));
            Check(!System.IO.File.Exists(target), "Reserved lock file created"); f.NoTemps();
        });
        // This restriction is export-only; ordinary ResourceSafety paths may contain tilde.
        Add("Export tilde leaf rejected without creating destination", f =>
        {
            var target = f.PathOf("VAULT~1.JSO");
            Reject(() => AtomicExport.WriteText(target, "bad", f.Vault));
            Check(!System.IO.File.Exists(target), "Tilde export destination created"); f.NoTemps();
        });
        Add("Export existing tilde leaf unchanged", f =>
        {
            var target = f.File("report~draft.txt");
            Protected(f, target, target, f.Vault);
        });
        Add("Resource existing tilde filename remains valid", f => Local(f.File("report~draft.txt"), true));
        Add("Export actual 8.3 protected leaf alias rejected", f =>
        {
            var target = Path.Combine(f.Vault, "vault.json");
            System.IO.File.WriteAllText(target, Original);
            var buffer = new StringBuilder(32768);
            var length = GetShortPathName(target, buffer, (uint)buffer.Capacity);
            Check(length != 0 && length < buffer.Capacity, "GetShortPathName failed; Win32=" + Marshal.GetLastWin32Error());
            var leaf = Path.GetFileName(buffer.ToString());
            if (!leaf.Contains('~')) throw new Skipped("Filesystem returned no tilde 8.3 leaf alias; short-name generation unavailable.");
            // Keep the long parent so the test specifically exercises the leaf alias.
            var alias = Path.Combine(f.Vault, leaf);
            Check(System.IO.File.ReadAllText(alias) == Original, "8.3 alias does not reach owned original");
            Protected(f, alias, target, f.Vault);
        });
        Add("Export safe sibling inside vault allowed", f => { var target = Path.Combine(f.Vault, "export.txt"); AtomicExport.WriteText(target, "ok", f.Vault); Check(System.IO.File.ReadAllText(target) == "ok", "Unexpected block"); f.NoTemps(); });
        Add("Export locked existing unchanged", f =>
        {
            var target = f.File("locked");
            using (var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject(() => AtomicExport.WriteText(target, "bad", f.Vault));
            Check(System.IO.File.ReadAllText(target) == Original, "Locked file changed"); f.NoTemps();
        });
        foreach (var code in new[] { 0xd800, 0xdc00 })
            Add($"Export surrogate U+{code:X4} does not truncate", f =>
            {
                var target = f.File("existing"); var failed = false;
                try { AtomicExport.WriteText(target, "prefix" + (char)code, f.Vault); } catch (EncoderFallbackException) { failed = true; }
                Check(failed, "Invalid Unicode accepted"); Check(System.IO.File.ReadAllText(target) == Original, "Encoding failure truncated file"); f.NoTemps();
            });
        Add("Export invalid surrogate creates nothing", f =>
        {
            var target = f.PathOf("missing"); Reject(() => AtomicExport.WriteText(target, new string((char)0xd800, 1), f.Vault));
            Check(!System.IO.File.Exists(target), "Bad encoding created target"); f.NoTemps();
        });
        Add("Export missing parent not created", f => { var parent = f.PathOf("absent"); Reject(() => AtomicExport.WriteText(Path.Combine(parent, "file"), "bad", f.Vault)); Check(!Directory.Exists(parent), "Created missing directory"); f.NoTemps(); });
        Add("Export directory destination leaves no temp", f => { var target = f.Dir("directory"); Reject(() => AtomicExport.WriteText(target, "bad", f.Vault)); Check(Directory.Exists(target), "Directory changed"); f.NoTemps(); });
        foreach (var junction in new[] { false, true })
        {
            var kind = junction ? "junction" : "symlink";
            Add("Export vault protected through " + kind + " parent", f =>
            {
                var target = Path.Combine(f.Vault, "vault.json"); System.IO.File.WriteAllText(target, Original);
                Protected(f, Path.Combine(f.DirAlias("alias", f.Vault, junction), "vault.json"), target, f.Vault);
            });
            Add("Export focus protected with " + kind + " vault argument", f =>
            {
                var target = Path.Combine(f.Vault, "focus.json"); System.IO.File.WriteAllText(target, Original);
                Protected(f, target, target, f.DirAlias("alias", f.Vault, junction));
            });
        }
        Add("Export vault file symlink rejected", f =>
        {
            var target = Path.Combine(f.Vault, "vault.json"); System.IO.File.WriteAllText(target, Original);
            Protected(f, f.FileAlias("alias.txt", target, false), target, f.Vault);
        });
        Add("Export vault hardlink cannot overwrite protected inode", f =>
        {
            var target = Path.Combine(f.Vault, "vault.json"); System.IO.File.WriteAllText(target, Original);
            var alias = f.FileAlias("alias.txt", target, true);
            // Atomic replacement may safely detach the alias; original must never change.
            var rejected = Rejected(() => AtomicExport.WriteText(alias, "export", f.Vault));
            Check(System.IO.File.ReadAllText(target) == Original, "SECURITY: hardlink export overwrote protected inode");
            Check(System.IO.File.ReadAllText(alias) == (rejected ? Original : "export"), "Unexpected alias content"); f.NoTemps();
        });
    }
    // Indirect verification of the FileShare.None primitive, not an App integration test.
    // The only executable launched here is this test binary; junction setup uses cmd mklink /J.
    private static void Locks()
    {
        Add("Physical lock two-process same path", f => { var target = f.File("app.lock"); Lock(f, target, target); });
        foreach (var junction in new[] { false, true })
            Add("Physical lock two-process " + (junction ? "junction" : "directory symlink"), f =>
            {
                var directory = f.Dir("real"); var target = Path.Combine(directory, "app.lock"); System.IO.File.WriteAllText(target, Original);
                Lock(f, target, Path.Combine(f.DirAlias("alias", directory, junction), "app.lock"));
            });
        foreach (var hard in new[] { false, true })
            Add("Physical lock two-process " + (hard ? "hardlink" : "file symlink"), f => { var target = f.File("app.lock"); Lock(f, target, f.FileAlias("alias.lock", target, hard)); });
    }
    private static void Lock(Fixture f, string target, string alias)
    {
        using (var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(Child(f.Root, alias) == 23, "Second process not denied by sharing violation");
        Check(Child(f.Root, alias) == 0, "Lock not released or alias invalid"); Check(System.IO.File.ReadAllText(target) == Original, "Lock probe altered file");
    }
    private static int Child(string root, string path)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--probe-lock"); start.ArgumentList.Add(root); start.ArgumentList.Add(path);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start self lock probe");
        Wait(child); return child.ExitCode;
    }
    private static int ProbeLock(string root, string path)
    {
        root = Path.GetFullPath(root); var parent = Path.GetDirectoryName(root)!;
        if (!Path.GetFileName(parent).StartsWith(".fixtures-", StringComparison.Ordinal) || !string.Equals(Path.GetDirectoryName(parent), Path.Combine(Workspace(), "native", "Relay.SafetyTests"), StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 64;
        try { using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return 0; }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return 23; }
        catch { return 65; }
    }
    private static void Wait(Process process)
    {
        if (process.WaitForExit(10000)) return;
        process.Kill(true); process.WaitForExit(); throw new IOException("Fixture child timed out and was terminated");
    }
    private static void Protected(Fixture f, string destination, string original, string vault)
    {
        var rejected = Rejected(() => AtomicExport.WriteText(destination, "bad export", vault));
        Check(System.IO.File.ReadAllText(original) == Original, "SECURITY: protected original overwritten via alias");
        Check(rejected, "SECURITY: protected alias accepted"); f.NoTemps();
    }
    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { return true; }
    }
    private static void Reject(Action action) => Check(Rejected(action), "Unsafe operation succeeded");
    private static void Local(string path, bool expected, bool missing = false, bool redirected = false)
    {
        var actual = ResourceSafety.IsLocalNonRedirected(path, out var reason, missing);
        Check(actual == expected, expected ? "Valid path rejected: " + reason : "SECURITY: unsafe path accepted");
        Check(expected ? string.IsNullOrEmpty(reason) : !string.IsNullOrWhiteSpace(reason), "Incorrect reason state");
        if (redirected) Check(reason.Contains("重定向", StringComparison.Ordinal) || reason.Contains("链接", StringComparison.Ordinal), "Did not reject at reparse ancestor: " + reason);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Workspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "native", "Relay.SafetyTests", "Relay.SafetyTests.csproj"))) return dir.FullName;
        throw new IOException("Run test from its workspace build output");
    }
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
    private sealed class Skipped(string message) : Exception(message);
    private sealed class Fixture : IDisposable
    {
        private readonly List<(string Path, bool Directory)> _aliases = [];
        public string Root { get; }
        public string Vault { get; }
        public Fixture(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); Vault = Dir("vault"); }
        public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
        public string Dir(string name) { var path = PathOf(name); Directory.CreateDirectory(path); return path; }
        public string File(string name) { var path = PathOf(name); System.IO.File.WriteAllText(path, Original); return path; }
        private void Own(string path) => Check(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Path outside owned fixture");
        public string DirAlias(string name, string target, bool junction)
        {
            var alias = PathOf(name); Own(alias); Own(target);
            if (!junction)
            {
                try { Directory.CreateSymbolicLink(alias, target); }
                catch (Exception ex) when (NoSymlinkPrivilege(ex)) { throw new Skipped("Symlink privilege unavailable: " + ex.Message); }
            }
            else
            {
                Check(!alias.Any(ShellMeta) && !target.Any(ShellMeta), "Unsafe junction path character");
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
                // cmd.exe parses a command line, not CRT-escaped ArgumentList entries.
                start.Arguments = $"/d /c mklink /J \"{alias}\" \"{target}\"";
                using var child = Process.Start(start) ?? throw new IOException("Cannot create junction"); Wait(child);
                Check(child.ExitCode == 0, "mklink failed, exit=" + child.ExitCode);
            }
            _aliases.Add((alias, true)); Check((System.IO.File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "Not a reparse point"); return alias;
        }
        public string FileAlias(string name, string target, bool hard)
        {
            var alias = PathOf(name); Own(alias); Own(target);
            if (hard)
            {
                if (!CreateHardLink(alias, target, IntPtr.Zero)) throw new IOException("CreateHardLink Win32=" + Marshal.GetLastWin32Error());
            }
            else
            {
                try { System.IO.File.CreateSymbolicLink(alias, target); }
                catch (Exception ex) when (NoSymlinkPrivilege(ex)) { throw new Skipped("Symlink privilege unavailable: " + ex.Message); }
            }
            _aliases.Add((alias, false)); return alias;
        }
        public void NoTemps()
        {
            var pending = new Stack<string>(); pending.Push(Root);
            while (pending.TryPop(out var dir)) foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                Check(!Path.GetFileName(entry).StartsWith(".relay-export-", StringComparison.Ordinal), "Leaked export temp");
                var attributes = System.IO.File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory) pending.Push(entry);
            }
        }
        public void Dispose()
        {
            // Unlink known aliases before recursive cleanup, never recurse through their targets.
            foreach (var (path, directory) in _aliases.AsEnumerable().Reverse()) { Own(path); if (directory) Directory.Delete(path, false); else System.IO.File.Delete(path); }
            Check(Path.GetFileName(Path.GetDirectoryName(Root)!).StartsWith(".fixtures-", StringComparison.Ordinal), "Unsafe cleanup root");
            Directory.Delete(Root, true);
        }
        private static bool ShellMeta(char c) => c is '"' or '&' or '|' or '<' or '>' or '^' or '%' or '!' || char.IsControl(c);
        private static bool NoSymlinkPrivilege(Exception ex) => ex is UnauthorizedAccessException or PlatformNotSupportedException || ex is IOException && (ex.HResult & 0xffff) is 5 or 1314;
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);
    }
}
