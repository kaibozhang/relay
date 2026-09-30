using System.Text.Json;
using Relay.Core;
using Relay.Desktop.Services;

// Links the production ResourceOpenPolicy and ResourceSafety, not a copy of their decisions.
// Decision tests ONLY: no WPF, Process.Start, shell execution, browser, HTTP client, DNS,
// listener, mapped drive, or external helper. Files below the disposable fixture are inert.
// These tests do not claim to sandbox OS associations or prove the launch/confirmation UI.
internal static class Program
{
    private static readonly List<(string Name, Action<Fixture> Run)> Tests = [];
    private static readonly string[] Documents = [".txt", ".md", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".log", ".json", ".xml", ".yaml", ".yml", ".cs", ".ts", ".css"];
    private const string Payload = "Inert fixture only — no application opens this content.";
    private static void Add(string name, Action<Fixture> run) => Tests.Add((name, run));

    public static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("TOTAL pass=0 skip=50 fail=0 (Windows path semantics required)");
            return 0;
        }
        Register();
        Check(Tests.Count == 50, "Expected exactly 50 named decision cases");
        var root = Path.Combine(ProjectDirectory(), ".fixtures-" + Guid.NewGuid().ToString("N"));
        Check(new DriveInfo(Path.GetPathRoot(root)!).DriveType is DriveType.Fixed or DriveType.Removable, "Fixture requires a local drive");
        Directory.CreateDirectory(root);
        Console.WriteLine("Decision-only suite; no actual resource launch or network fetch. Fixture: " + root);
        int pass = 0, skip = 0, fail = 0, index = 0;
        foreach (var (name, run) in Tests)
        {
            Fixture? fixture = null;
            try
            {
                fixture = new Fixture(Path.Combine(root, (++index).ToString("D2")));
                run(fixture); pass++; Console.WriteLine("PASS " + name);
            }
            catch (Skipped ex) { skip++; Console.WriteLine("SKIP " + name + ": " + ex.Message); }
            catch (Exception ex) { fail++; Console.WriteLine("FAIL " + name + ": " + ex); }
            finally
            {
                try { fixture?.Dispose(); }
                catch (Exception ex) { fail++; Console.WriteLine("FAIL cleanup: " + ex); }
            }
        }
        try
        {
            Check(Path.GetDirectoryName(root) == ProjectDirectory() && Path.GetFileName(root).StartsWith(".fixtures-", StringComparison.Ordinal), "Unsafe cleanup root");
            Directory.Delete(root, false);
        }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL root cleanup: " + ex); }
        Console.WriteLine($"TOTAL pass={pass} skip={skip} fail={fail}; named cases={Tests.Count}; document variants=72");
        return fail == 0 ? 0 : 1;
    }

    private static void Register()
    {
        // 1–3: every entry in the independently specified allowlist, including case folding.
        Add("all 24 lowercase document extensions require confirmation", f =>
        {
            foreach (var extension in Documents) Document(f.File("document" + extension));
        });
        Add("all 24 uppercase document extensions require confirmation", f =>
        {
            foreach (var extension in Documents) Document(f.File("document" + extension.ToUpperInvariant()));
        });
        Add("all 24 mixed-case document extensions require confirmation", f =>
        {
            foreach (var extension in Documents) Document(f.File("document." + char.ToUpperInvariant(extension[1]) + extension[2..]));
        });
        // 4–19: dangerous/active/shortcut formats must NEVER become FileName.
        foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".py", ".html", ".svg", ".lnk", ".url", ".reg", ".msi", ".scr", ".hta" })
            Add(extension + " is reveal-only (including uppercase)", f =>
            {
                Reveal(f.File("inert" + extension));
                Reveal(f.File("UPPER" + extension.ToUpperInvariant()));
            });
        Add("last extension, not earlier document suffix, controls decision", f =>
        {
            Reveal(f.File("invoice.pdf.exe")); Reveal(f.File("notes.txt.lnk"));
            Document(f.File("inert.exe.txt"));
        });
        Add("extensionless file is reveal-only", f => Reveal(f.File("README")));
        Add("unknown extension is reveal-only", f => Reveal(f.File("unknown.relay-test-unknown")));
        Add("document shell metacharacters remain a literal filename", f => Document(f.File("name & echo NO ^ %PATH% ! (x); , = $.txt")));
        Add("unsafe shell metacharacters stay inside one Explorer argument", f => Reveal(f.File("name & echo NO ^ %PATH% ! (x); , = $.cmd")));
        Add("Windows spaces and Unicode are not manually shell-quoted", f => Document(f.File("space folder 中文 🧪 document.txt")));
        Add("surrounding Windows quotes are rejected instead of interpreted", f => Reject(Ref("file", "\"" + f.File("space name.txt") + "\"")));
        Add("embedded quote or appended command is rejected", f => Reject(Ref("file", f.File("safe.txt") + "\" & echo NO")));
        Add("explicit reveal overrides a safe document association", f => Reveal(f.File("safe.txt"), explicitReveal: true));
        Add("existing folder opens directly after explicit selection", f => Folder(f.Dir("folder")));
        Add("explicit folder reveal is a single literal select argument", f => Reveal(f.Dir("folder"), "folder", true));
        Add("folder suffix is not treated as executable or document", f => { Folder(f.Dir("folder.exe")); Folder(f.Dir("folder.txt")); });
        Add("file-folder type mismatches fail closed", f =>
        {
            Reject(Ref("folder", f.File("file.txt"))); Reject(Ref("file", f.Dir("folder")));
        });
        Add("missing paths reject for both open and reveal", f =>
        {
            foreach (var kind in new[] { "file", "folder" })
                foreach (var reveal in new[] { false, true }) Reject(Ref(kind, f.PathOf("missing")), reveal);
        });
        Add("unknown or case-changed resource kinds reject", f =>
        {
            foreach (var kind in new[] { "", "exe", "protocol", "FILE", "URL" }) Reject(Ref(kind, f.File("safe.txt")));
        });
        Add("relative UNC device wildcard and reserved paths reject lexically", f =>
        {
            foreach (var target in new[] { "", " ", "relative.txt", @"C:relative.txt", @"\rooted.txt", @"\\relay-launcher.invalid\share\never-contacted", "//relay-launcher.invalid/share/never-contacted", @"\\?\C:\never-contacted", @"\\.\C:\never-contacted", f.PathOf("CON.txt"), f.PathOf("bad*.txt"), f.PathOf("bad|name.txt"), f.PathOf("bad?.txt"), f.PathOf("trailing.txt.") })
                Reject(Ref("file", target));
        });
        Add("path controls reject before metadata lookup", f =>
        {
            foreach (var code in new[] { 0, 9, 10, 13, 31, 127 }) Reject(Ref("file", f.PathOf("bad" + (char)code + ".txt")));
        });
        Add("alternate data stream cannot reach an association", f => Reject(Ref("file", f.File("safe.txt") + ":payload.exe")));
        Add("reparse folder leaf rejects open and reveal", f =>
        {
            var alias = f.DirectoryAlias("alias", f.Dir("target"));
            Reject(Ref("folder", alias)); Reject(Ref("folder", alias), true);
        });
        Add("reparse ancestor rejects existing and missing descendants", f =>
        {
            var target = f.Dir("target"); System.IO.File.WriteAllText(Path.Combine(target, "safe.txt"), Payload);
            var alias = f.DirectoryAlias("alias", target);
            Reject(Ref("file", Path.Combine(alias, "safe.txt"))); Reject(Ref("file", Path.Combine(alias, "missing.txt")));
        });
        Add("reparse document file cannot reach default association", f =>
        {
            var alias = f.FileAlias("alias.txt", f.File("target.txt"));
            Reject(Ref("file", alias)); Reject(Ref("file", alias), true);
        });
        Add("HTTPS URL requires confirmation even when reveal requested", _ =>
        {
            Url("https://example.invalid/path?q=1#fragment", "https://example.invalid/path?q=1#fragment");
            Url("https://example.invalid/path", "https://example.invalid/path", true);
        });
        Add("HTTP URL is allowed with confirmation", _ => Url("http://example.invalid/", "http://example.invalid/"));
        Add("localhost private and loopback URL plans allow explicit human choice", _ =>
        {
            foreach (var url in new[] { "http://localhost:43129/", "http://127.0.0.1:43129/", "http://[::1]:43129/", "http://192.168.1.1/", "http://10.0.0.1/" }) Url(url, url);
        });
        Add("URL parser canonicalizes without interpreting shell characters", _ =>
        {
            Url("HTTPS://EXAMPLE.INVALID:443/a b?q=x&y=z#part", "https://example.invalid/a%20b?q=x&y=z#part");
            Url("https://example.invalid/?q=%22%26%20calc.exe", "https://example.invalid/?q=%22%26%20calc.exe");
        });
        Add("URL userinfo and credential-bearing authority reject", _ =>
        {
            foreach (var url in new[] { "https://user:password@example.invalid/", "http://user@example.invalid/", "https://user%40other@example.invalid/", "https://user:pass@localhost/" }) Reject(Ref("url", url));
        });
        Add("custom protocols and active or local schemes reject", _ =>
        {
            foreach (var url in new[] { "relay-test:open", "ms-settings:display", "shell:AppsFolder", "javascript:alert(1)", "data:text/html,test", "file:///C:/Windows/notepad.exe", "ftp://example.invalid/a", "mailto:test@example.invalid" }) Reject(Ref("url", url));
        });
        Add("invalid URL and relative authority forms reject", _ =>
        {
            foreach (var url in new[] { "", " ", "not a URL", "/relative", "//example.invalid/path", "https://", "https://:443/", "http://[broken/", "https://example.invalid:99999/" }) Reject(Ref("url", url));
        });
        Add("URL control characters reject instead of silent URI trimming", _ =>
        {
            foreach (var code in new[] { 0, 9, 10, 13, 31, 127 }) Reject(Ref("url", "https://example.invalid/a" + (char)code + "b"));
        });
        Add("planning is metadata-only deterministic and does not modify resource", f =>
        {
            var path = f.File("locked.txt"); var resource = Ref("file", path);
            var before = JsonSerializer.Serialize(resource);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var one = ResourceOpenPolicy.Plan(resource); var two = ResourceOpenPolicy.Plan(resource);
                AssertPlan(one, path, [], true, true, "document");
                AssertPlan(two, one.FileName, one.Arguments, one.UseShellExecute, one.RequiresConfirmation, one.OpenKind);
                Check(JsonSerializer.Serialize(resource) == before, "Planning mutated ResourceRef");
                Check(held.Length == System.Text.Encoding.UTF8.GetByteCount(Payload), "Planning altered locked content");
            }
            Check(System.IO.File.ReadAllText(path) == Payload, "Planning changed file bytes");
            Check(Directory.GetFileSystemEntries(f.Root).Length == 1, "Planning created side-effect files");
        });
        Add("null and oversized target data fail closed", _ =>
        {
            Reject(Ref("url", null!)); Reject(Ref("file", null!));
            Reject(Ref("url", "https://example.invalid/" + new string('a', 32768)));
            try { ResourceOpenPolicy.Plan(null!); }
            catch (ArgumentNullException) { return; }
            throw new InvalidOperationException("Null resource accepted");
        });
    }

    private static ResourceRef Ref(string kind, string target) => new() { Kind = kind, Target = target, Label = "fixture only" };
    private static void Document(string path) => AssertPlan(ResourceOpenPolicy.Plan(Ref("file", path)), Path.GetFullPath(path), [], true, true, "document");
    private static void Folder(string path) => AssertPlan(ResourceOpenPolicy.Plan(Ref("folder", path)), Path.GetFullPath(path), [], true, false, "folder");
    private static void Url(string input, string expected, bool reveal = false) => AssertPlan(ResourceOpenPolicy.Plan(Ref("url", input), reveal), expected, [], true, true, "url");
    private static void Reveal(string path, string kind = "file", bool explicitReveal = false)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Check(Path.IsPathFullyQualified(explorer), "Explorer must not resolve via PATH or current directory");
        AssertPlan(ResourceOpenPolicy.Plan(Ref(kind, path), explicitReveal), explorer, ["/select," + Path.GetFullPath(path)], false, false, "reveal");
    }
    private static void AssertPlan(ResourceOpenPlan actual, string fileName, IReadOnlyList<string> arguments, bool shell, bool confirmation, string kind)
    {
        Check(actual.FileName == fileName, $"FileName: expected {fileName}, got {actual.FileName}");
        Check(actual.Arguments.SequenceEqual(arguments), "Arguments must remain separate exact literal values");
        Check(actual.UseShellExecute == shell, "Incorrect UseShellExecute");
        Check(actual.RequiresConfirmation == confirmation, "Incorrect confirmation requirement");
        Check(actual.OpenKind == kind, $"Expected {kind}, got {actual.OpenKind}");
        Check(!string.IsNullOrWhiteSpace(actual.Description), "Decision explanation missing");
    }
    private static void Reject(ResourceRef resource, bool reveal = false)
    {
        try { ResourceOpenPolicy.Plan(resource, reveal); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Check(!string.IsNullOrWhiteSpace(ex.Message), "Rejection reason missing"); return;
        }
        throw new InvalidOperationException("Unsafe resource produced an open plan: " + resource.Kind + " " + resource.Target);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string ProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Relay.LauncherTests.csproj"))) return dir.FullName;
        throw new IOException("Run the suite from its workspace build output");
    }
    private sealed class Skipped(string message) : Exception(message);
    private sealed class Fixture(string root) : IDisposable
    {
        private readonly List<(string Path, bool IsDirectory)> _aliases = [];
        public string Root { get; } = Directory.CreateDirectory(root).FullName;
        public string PathOf(string name) => Path.Combine(Root, name);
        public string File(string name) { var path = PathOf(name); System.IO.File.WriteAllText(path, Payload); return path; }
        public string Dir(string name) => Directory.CreateDirectory(PathOf(name)).FullName;
        public string DirectoryAlias(string name, string target) => Alias(name, target, true);
        public string FileAlias(string name, string target) => Alias(name, target, false);
        private void Own(string path) => Check(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Operation outside owned fixture");
        private string Alias(string name, string target, bool directory)
        {
            var alias = PathOf(name); Own(alias); Own(target);
            try
            {
                if (directory) Directory.CreateSymbolicLink(alias, target);
                else System.IO.File.CreateSymbolicLink(alias, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException || ex is IOException && (ex.HResult & 0xffff) is 5 or 1314)
            {
                throw new Skipped("Local symlink privilege unavailable; no simulated reparse result: " + ex.Message);
            }
            _aliases.Add((alias, directory));
            Check((System.IO.File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "Fixture is not a reparse point");
            return alias;
        }
        public void Dispose()
        {
            // Remove aliases themselves first; never recursively delete through a redirect.
            foreach (var (path, directory) in _aliases.AsEnumerable().Reverse())
            {
                Own(path); if (directory) Directory.Delete(path, false); else System.IO.File.Delete(path);
            }
            Check(Path.GetFileName(Path.GetDirectoryName(Root)!).StartsWith(".fixtures-", StringComparison.Ordinal), "Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
}
