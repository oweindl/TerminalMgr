using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TerminalMgr.Core;
using TerminalMgr.Windows;

if (args.Contains("--pty-child"))
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine("CHILD_READY");
    Console.WriteLine("TERM:" + Environment.GetEnvironmentVariable("TERM"));
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.KeyChar == 'q') return;
        if (key.KeyChar == 'r') Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
        else Console.WriteLine($"KEY:{(int)key.KeyChar}");
    }
}

var passed = 0;
var failures = new List<string>();
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
var root = Path.Combine(Path.GetTempPath(), "TerminalMgr-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Test("SSH config mode preserves RemoteCommand / RequestTTY", () =>
    {
        var a = SshCommand.Arguments(new() { Name = "dev", Destination = "linuxdev" });
        Check(!a.Contains("-tt") && !a.Contains("RemoteCommand=none") && a.Last() == "linuxdev", "Config mode overrides SSH config");
    });
    Test("tmux mode overrides RemoteCommand once and quotes new-session program", () =>
    {
        var a = SshCommand.Arguments(new() { Name = "dev", Destination = "dev", Mode = StartMode.Tmux, Command = "printf 'hello'; exec bash" });
        Check(a.Count(x => x == "RemoteCommand=none") == 1, "RemoteCommand not disabled exactly once");
        Check(a.Last().StartsWith("tmux new-session -A -s 'main' "), "Missing attach-or-create");
        Check(a.Last().Contains(SshCommand.ShellQuote("printf 'hello'; exec bash")), "Shell quotes missing");
    });
    Test("Command is passed as one argument without local shell", () =>
    {
        const string command = "echo $HOME; printf \"hello world\"";
        var a = SshCommand.Arguments(new() { Name = "dev", Destination = "dev", Mode = StartMode.Command, Command = command });
        Check(a.Last() == command, "Command was changed");
        Check(a.Contains("-tt"), "No interactive PTY");
    });
    Test("Reject destination option injection", () =>
    {
        foreach (string value in new[] { "-oProxyCommand=evil", "dev -p 22", "dev\nother" })
        {
            bool rejected = false;
            try { new Profile { Name = "x", Destination = value }.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Accepted " + value);
        }
    });
    Test("Windows quoting preserves trailing slash and embedded quotes", () =>
    {
        Check(SshCommand.WindowsQuote("C:\\foo bar\\") == "\"C:\\foo bar\\\\\"", "Trailing slash");
        Check(SshCommand.WindowsQuote("a\"b") == "\"a\\\"b\"", "Embedded quote");
        Check(SshCommand.WindowsQuote("") == "\"\"", "Empty argument");
    });
    Test("Import multi-alias Host blocks, deduplicate and ignore patterns", () =>
    {
        var config = Path.Combine(root, "config");
        File.WriteAllText(config, "Host linuxdev\n HostName dev.local\nHost linuxdev webserver webtest omv contabo\n RequestTTY yes\n RemoteCommand tmux new -A -s main\nHost * !bad *.local\nHost linuxdev-code # VS Code\n RequestTTY no\n");
        var imported = SshConfigImporter.Import(config);
        Check(imported.Count == 6, "Wrong alias count: " + imported.Count);
        Check(imported.All(p => p.Mode == StartMode.SshConfig && !p.AutoConnect && p.ConfigPath == config), "Unsafe import defaults");
    });
    Test("Import Include files and stop recursion", () =>
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        File.WriteAllText(first, $"Host one\nInclude \"{second}\"\n");
        File.WriteAllText(second, $"Host two\nInclude \"{first}\"\n");
        Check(SshConfigImporter.Import(first).Count == 2, "Include cycle or missing aliases");
    });
    Test("Settings round-trip includes autostart and pinned multiline clipboard", () =>
    {
        var store = new SettingsStore(Path.Combine(root, "settings"));
        var profile = new Profile { Name = "dev", Destination = "dev", AutoConnect = true, Mode = StartMode.Tmux };
        store.Save(new() { Profiles = [profile], PinnedClips = [new("a\nb", "dev", DateTimeOffset.Now, true)], FontSize = 17 });
        var loaded = store.Load();
        Check(loaded.Profiles.Single() == profile && loaded.PinnedClips.Single().Text == "a\nb" && loaded.FontSize == 17, "Round-trip failed");
    });
    Test("Corrupt settings are not silently discarded", () =>
    {
        var directory = Path.Combine(root, "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{");
        bool rejected = false;
        try { new SettingsStore(directory).Load(); } catch (System.Text.Json.JsonException) { rejected = true; }
        Check(rejected, "Corrupt data accepted");
    });

    Test("Old settings file without new fields loads with defaults", () =>
    {
        var directory = Path.Combine(root, "old");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Profiles\":[],\"PinnedClips\":[],\"FontSize\":15}");
        var loaded = new SettingsStore(directory).Load();
        Check(loaded.FontSize == 15 && loaded.ClipHistoryLimit == 100, "Defaults missing");
    });
    Test("User data and models stay outside the installation folder", () =>
    {
        string data = AppPaths.DataDirectory();
        Check(!AppPaths.IsInside(data, AppContext.BaseDirectory), "Data inside install folder");
        Check(AppPaths.IsInside(AppPaths.ModelsDirectory(), data) && AppPaths.ModelsDirectory().EndsWith("models"), "Models outside data folder");
        Check(AppPaths.IsInside(Path.Combine(data, "settings.json"), data) && !AppPaths.IsInside(data + "-other", data), "IsInside is wrong");
    });
    Test("Clipboard list: newest first and new copy becomes active", () =>
    {
        var list = new ClipboardList();
        list.Add("one", "dev"); list.Add("two", "Windows");
        Check(list.Items.Select(i => i.Text).SequenceEqual(["two", "one"]) && list.Active!.Text == "two", "Order or active entry wrong");
    });
    Test("Clipboard list: duplicate moves up, keeps source and does not add a second entry", () =>
    {
        var list = new ClipboardList();
        list.Add("one", "dev"); list.Add("two", "Windows"); list.Add("one", "Windows");
        Check(list.Items.Count == 2 && list.Items[0].Text == "one" && list.Items[0].Source == "dev" && list.Active!.Text == "one", "Duplicate handling wrong");
    });
    Test("Clipboard list: older entry can be made active without reordering", () =>
    {
        var list = new ClipboardList();
        list.Add("one", "dev"); list.Add("two", "dev"); list.Add("three", "dev");
        Check(list.Activate(list.Items[2]) && list.Active!.Text == "one" && list.Items[0].Text == "three", "Activation reordered or failed");
        list.Add("four", "dev");
        Check(list.Active!.Text == "four", "New copy must become active again");
    });
    Test("Clipboard list: pinned entries survive limit and duplicates keep the pin", () =>
    {
        var list = new ClipboardList { MaxUnpinned = 2 };
        list.Add("keep", "dev"); list.TogglePin(list.Items[0]);
        list.Add("a", "dev"); list.Add("b", "dev"); list.Add("c", "dev");
        Check(list.Items.Select(i => i.Text).SequenceEqual(["c", "b", "keep"]), "Limit trimmed pinned or kept too many: " + string.Join(",", list.Items.Select(i => i.Text)));
        list.Add("keep", "Windows");
        Check(list.Items[0].Text == "keep" && list.Items[0].Pinned && list.Items.Count == 3, "Pinned duplicate lost its pin");
    });
    Test("Clipboard list: remove of the active entry falls back to the newest; empty and huge texts are rejected", () =>
    {
        var list = new ClipboardList();
        list.Add("one", "dev"); list.Add("two", "dev");
        list.Remove(list.Active!);
        Check(list.Active!.Text == "one", "No fallback");
        Check(list.Add("", "dev") is null && list.Add(new string('x', ClipboardList.MaxTextLength + 1), "dev") is null, "Invalid text accepted");
    });
    Test("Clipboard list: only pinned entries are restored from settings", () =>
    {
        var list = new ClipboardList();
        list.LoadPinned([new("p", "dev", DateTimeOffset.Now, true), new("loose", "dev", DateTimeOffset.Now, false)]);
        Check(list.Items.Count == 1 && list.Items[0].Text == "p", "Unpinned restored");
    });
    Test("Copy text loses padding spaces per line and uses \\n", () =>
    {
        Check(ClipboardText.TrimTrailingSpaces("ls   \r\n  indent  \r\n\r\nend ") == "ls\n  indent\n\nend", "Trim wrong");
    });
    Test("Preview shows two lines, ellipsis and total size", () =>
    {
        var many = ClipboardText.Preview(string.Join('\n', Enumerable.Range(1, 42).Select(i => "line " + i)));
        Check(many.Line1 == "line 1" && many.Line2 == "line 2 …" && many.LengthLabel == "42 Zeilen", "Multi-line preview wrong: " + many);
        var two = ClipboardText.Preview("a\nb");
        Check(two.Line2 == "b" && two.LengthLabel == "", "Two lines must not be marked");
        var wide = ClipboardText.Preview(new string('x', 500));
        Check(wide.Line1.EndsWith("…") && wide.Line1.Length < 130 && wide.Line2 is null && wide.LengthLabel == "500 Zeichen", "Long line preview wrong");
        Check(ClipboardText.Preview("short") == new ClipPreview("short", null, ""), "Short text altered");
    });
    Test("Tooltip excerpt is bounded", () =>
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => "l" + i));
        var excerpt = ClipboardText.Excerpt(text);
        Check(excerpt.EndsWith("…") && excerpt.Split('\n').Length <= 21, "Excerpt not bounded");
        Check(ClipboardText.Excerpt("x") == "x", "Short excerpt changed");
    });
    Test("Paste text keeps line breaks and tabs but drops control characters", () =>
    {
        Check(ClipboardText.StripControlCharacters("a\r\nb\tc\x1b[31m\x03\0d\u2028e") == "a\nb\tc[31mde", "Control characters survived");
    });
    Test("Clipboard capture ignores password-manager and own writes", () =>
    {
        Check(ClipboardCapturePolicy.ShouldCapture(true, false, null, false), "Plain text refused");
        Check(ClipboardCapturePolicy.ShouldCapture(true, false, 1, false), "CanInclude=1 refused");
        Check(!ClipboardCapturePolicy.ShouldCapture(true, true, null, false), "Exclude format captured");
        Check(!ClipboardCapturePolicy.ShouldCapture(true, false, 0, false), "CanInclude=0 captured");
        Check(!ClipboardCapturePolicy.ShouldCapture(true, false, null, true), "Own write captured");
        Check(!ClipboardCapturePolicy.ShouldCapture(false, false, null, false), "Non-text captured");
    });

    Test("tmux list parses stable IDs, names and counts; ignores banners", () =>
    {
        var rows = TmuxSessionParser.Parse("Welcome\n$1\twork ; echo nope\t3\t1\n$0\tmain\t2\t0\ninvalid\trow\n");
        Check(rows.Count == 2 && rows[0].Name == "main" && rows[1].Windows == 3, "Bad session parsing");
    });
    Test("Existing tmux attach uses stable ID and never creates a session", () =>
    {
        var a = SshCommand.TmuxAttachArguments(new() { Name = "dev", Destination = "dev" }, "$42");
        Check(a.Last() == "tmux attach-session -t '$42'", "Wrong target");
        Check(a.Contains("RemoteCommand=none") && a.Contains("-tt"), "Missing config override");
        bool rejected = false;
        try { SshCommand.TmuxAttachArguments(new() { Name = "dev", Destination = "dev" }, "$1; echo bad"); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unsafe ID accepted");
    });
    Test("tmux query cannot accidentally attach or prompt for passwords", () =>
    {
        var a = SshCommand.TmuxListArguments(new() { Name = "dev", Destination = "dev" });
        Check(a.Contains("-T") && a.Contains("BatchMode=yes") && a.Contains("RemoteCommand=none"), "Query can run interactive config command");
    });
    Test("tmux window list parses IDs, index, active flag, session, name and command", () =>
    {
        var rows = TmuxWindowParser.Parse("$0\t@8\t0\t0\tclaude\tclaude\tclaude\n$0\t@10\t8\t1\tclaude\teditor\tvim\r\n$1\t@9\t0\t1\tmain\tbash\tbash\n");
        Check(rows.Count == 3, "Count " + rows.Count);
        Check(rows[1] == new TmuxWindowRecord("$0", "claude", "@10", 8, "editor", "vim", true), "Row 2 wrong: " + rows[1]);
        Check(!rows[0].Active && rows[2].Active && rows[2].SessionName == "main", "Active flag or session wrong");
    });
    Test("tmux window list survives tabs, quotes, semicolons and Unicode in names", () =>
    {
        var rows = TmuxWindowParser.Parse("$3\t@4\t2\t0\tmy session\ta\tb\tc ; $(rm -x) 'q' \"d\" äöü 日本\tvim\n");
        Check(rows.Count == 1 && rows[0].SessionId == "$3" && rows[0].WindowId == "@4" && rows[0].Index == 2, "IDs shifted by tabs");
        Check(rows[0].SessionName == "my session" && rows[0].Name == "a\tb\tc ; $(rm -x) 'q' \"d\" äöü 日本" && rows[0].Command == "vim", "Name or command wrong: " + rows[0]);
    });
    Test("tmux window list: no sessions, banners and malformed rows give nothing", () =>
    {
        Check(TmuxWindowParser.Parse("").Count == 0 && TmuxWindowParser.Parse("no server running on /tmp/tmux-1000/default\n").Count == 0, "Empty output");
        string bad = "Welcome\n$x\t@1\t0\t1\ts\tn\tc\n$1\t1\t0\t1\ts\tn\tc\n$1\t@1\t-1\t1\ts\tn\tc\n$1\t@1\t0\t2\ts\tn\tc\n$1\t@1\t0\t1\ts\tn\n";
        Check(TmuxWindowParser.Parse(bad).Count == 0, "Malformed rows accepted");
    });
    Test("tmux windows are grouped per session, sorted, labelled and switched to submenus when many", () =>
    {
        var rows = TmuxWindowParser.Parse("$1\t@9\t0\t1\tmain\tbash\tbash\n$0\t@2\t2\t0\tClaude\tdocs\tvim\n$0\t@1\t1\t1\tClaude\tdocs\tdocs\n");
        var groups = TmuxWindowParser.Group(rows);
        Check(groups.Select(g => g.Name).SequenceEqual(["Claude", "main"]) && groups[0].Windows.Select(w => w.Index).SequenceEqual([1, 2]), "Grouping wrong");
        Check(groups[0].Windows[0].Label == "1: docs" && groups[0].Windows[1].Label == "2: docs (vim)", "Labels wrong");
        Check(!TmuxWindowParser.UseSubmenus(groups), "Small list must stay flat");
        var many = Enumerable.Range(1, 24).Select(i => new TmuxWindowRecord("$0", "s", "@" + i, i, "w" + i, "bash", false)).ToList();
        Check(!TmuxWindowParser.UseSubmenus(TmuxWindowParser.Group(many)), "25 entries (24 windows + header) must stay flat");
        many.Add(new("$0", "s", "@25", 25, "w25", "bash", false));
        Check(TmuxWindowParser.UseSubmenus(TmuxWindowParser.Group(many)), "26 entries must use submenus");
    });
    Test("tmux window switch is sent in two steps with stable IDs only", () =>
    {
        var steps = TmuxWindowParser.SwitchSteps("$1", "@2");
        Check(steps.Count == 2 && steps[0] == "\x02:" && steps[1] == "switch-client -t '$1:@2'\r", "Steps: " + string.Join("|", steps));
        Check(TmuxWindowParser.StepDelay >= TimeSpan.FromMilliseconds(100), "Delay too short for tmux to open the prompt");
        foreach (var (s, w) in new[] { ("$1; echo x", "@2"), ("main", "@2"), ("$1", "@a"), ("$1", "2"), ("$1", "@2'; x") })
        {
            bool rejected = false;
            try { TmuxWindowParser.SwitchSteps(s, w); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Accepted " + s + " / " + w);
        }
    });
    Test("tmux window query and direct attach arguments are non-interactive / safe", () =>
    {
        var profile = new Profile { Name = "dev", Destination = "dev" };
        var q = SshCommand.TmuxWindowsArguments(profile);
        Check(q.Contains("-T") && q.Contains("BatchMode=yes") && q.Contains("RemoteCommand=none") && q.Contains("ConnectTimeout=8"), "Query may prompt");
        Check(q.Last().StartsWith("tmux list-windows -a -F '") && q.Last().Contains("#{window_id}"), "Wrong command: " + q.Last());
        var a = SshCommand.TmuxAttachArguments(profile, "$1", "@2");
        Check(a.Last() == "tmux attach-session -t '$1:@2'" && a.Contains("-tt") && a.Contains("RemoteCommand=none"), "Attach: " + a.Last());
        bool rejected = false;
        try { SshCommand.TmuxAttachArguments(profile, "$1", "@2; x"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unsafe window ID accepted");
        Check(SshCommand.EffectiveConfigArguments(new() { Name = "d", Destination = "dev", ConfigPath = "C:\\c" }).SequenceEqual(["-F", "C:\\c", "-G", "dev"]), "ssh -G arguments");
    });
    Test("Session name is found in typical tmux RemoteCommands", () =>
    {
        Check(TmuxWindowParser.SessionNameFromCommand("tmux new -A -s main") == "main", "new -A -s");
        Check(TmuxWindowParser.SessionNameFromCommand("tmux new-session -A -s 'work'") == "work", "new-session quoted");
        Check(TmuxWindowParser.SessionNameFromCommand("tmux attach -t main") == "main", "attach -t");
        Check(TmuxWindowParser.SessionNameFromCommand("tmux new -As dev") == "dev", "combined flags");
        Check(TmuxWindowParser.SessionNameFromCommand("bash -lc 'tmux new -A -s x'") == "x", "inside shell");
        Check(TmuxWindowParser.SessionNameFromCommand("tmux new -A") is null && TmuxWindowParser.SessionNameFromCommand("echo hi") is null
            && TmuxWindowParser.SessionNameFromCommand(null) is null && TmuxWindowParser.SessionNameFromCommand("  ") is null, "No name expected");
    });
    try
    {
        var output = new StringBuilder();
        var outputLock = new object();
        async Task WaitFor(string expected)
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeout)
            {
                lock (outputLock) if (output.ToString().Contains(expected)) return;
                await Task.Delay(25);
            }
            lock (outputLock) throw new Exception("Missing " + expected + " in " + output);
        }
        var session = new PtySession();
        session.Output += s => { lock (outputLock) output.Append(s); };
        string host = Environment.ProcessPath!;
        string assembly = Assembly.GetExecutingAssembly().Location;
        var arguments = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { assembly, "--pty-child" } : new[] { "--pty-child" };
        session.Start(host, arguments, 90, 30);
        try
        {
            await WaitFor("CHILD_READY");
            await WaitFor("TERM:xterm-256color");
            passed++; Console.WriteLine("PASS Child receives xterm-256color terminal type");
            await session.SendAsync("\x02");
            await WaitFor("KEY:2");
            passed++; Console.WriteLine("PASS Real ConPTY forwards Ctrl+B byte");
            session.Resize(132, 47);
            await session.SendAsync("r");
            await WaitFor("SIZE:132x47");
            passed++; Console.WriteLine("PASS Real ConPTY resize reaches child console");
            await session.SendAsync("q");
        }
        finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        passed++; Console.WriteLine("PASS Real ConPTY shutdown drains output without hanging");
    }
    catch (Exception ex) { failures.Add("ConPTY integration: " + ex.Message); Console.WriteLine("FAIL " + ex); }
}
finally { Directory.Delete(root, recursive: true); }
if (args.Contains("--ssh-smoke"))
{
    var session = new PtySession();
    var transcript = new StringBuilder();
    var transcriptLock = new object();
    var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    session.Output += text => { lock (transcriptLock) transcript.Append(text); };
    session.Exited += code => exited.TrySetResult(code);
    try
    {
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        session.Start(ssh, ["-tt", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
            "linuxdev", "printf 'TERM:%s\n' \"$TERM\"; stty size; tput clear && printf 'TERMINFO_OK\n'; tmux -V"], 132, 47);
        int code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        string output;
        lock (transcriptLock) output = transcript.ToString();
        Check(code == 0 && output.Contains("TERM:xterm-256color") && output.Contains("47 132") && output.Contains("TERMINFO_OK"),
            "Remote smoke failed (exit " + code + "): " + output);
        passed++; Console.WriteLine("PASS Real SSH linuxdev: TERM, remote PTY size, terminfo and tmux available");
        var sessions = await new TmuxSessionQuery().ListAsync(new() { Name = "linuxdev", Destination = "linuxdev" });
        Check(sessions.Count > 0, "Expected existing tmux sessions on linuxdev");
        passed++; Console.WriteLine("PASS Real SSH tmux session discovery: " + sessions.Count + " sessions");
        passed += await RealTmuxWindowScenario();
    }
    catch (Exception ex) { failures.Add("SSH smoke: " + ex.Message); Console.WriteLine("FAIL " + ex.Message); }
    finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
}
Console.WriteLine($"{passed} passed, {failures.Count} failed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

// Real tmux on linuxdev with throw-away sessions (tmgr-ta / tmgr-tb); the user's own sessions are never touched.
async Task<int> RealTmuxWindowScenario()
{
    int checks = 0;
    var profile = new Profile { Name = "linuxdev", Destination = "linuxdev" };
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    async Task<string> Remote(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "linuxdev", command }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return await stdout + await stderr;
    }
    async Task<string> ClientsOf(string session) => (await Remote($"tmux list-clients -F '#{{client_session}} #{{window_id}}' | grep '^{session} '")).Trim();
    Func<Task<string>>? diagnose = null;
    async Task Eventually(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(250); }
        throw new Exception("Timeout: " + message + (diagnose is null ? "" : "; " + await diagnose()));
    }
    PtySession? client = null;
    try
    {
        await Remote("tmux kill-session -t tmgr-ta 2>/dev/null; tmux kill-session -t tmgr-tb 2>/dev/null; true");
        await Remote("tmux new-session -d -s tmgr-ta -n one && tmux new-window -t tmgr-ta -n two 'cat' && tmux new-session -d -s tmgr-tb -n alpha && tmux new-window -t tmgr-tb -n beta");
        var windows = await new TmuxSessionQuery().ListWindowsAsync(profile);
        var ta = windows.Where(w => w.SessionName == "tmgr-ta").ToList();
        var tb = windows.Where(w => w.SessionName == "tmgr-tb").ToList();
        Check(ta.Count == 2 && tb.Count == 2, "Test sessions not listed: " + string.Join(";", windows.Select(w => w.SessionName + "/" + w.Name)));
        Check(ta.Single(w => w.Name == "two").Command == "cat" && ta.Single(w => w.Active).Name == "two", "Command or active flag wrong");
        Check(windows.Any(w => w.SessionName == "main"), "Existing session main not listed");
        string taId = ta[0].SessionId, tbId = tb[0].SessionId;
        string one = ta.Single(w => w.Name == "one").WindowId, beta = tb.Single(w => w.Name == "beta").WindowId, alpha = tb.Single(w => w.Name == "alpha").WindowId;
        checks++; Console.WriteLine("PASS Real tmux: window list with IDs, commands and active flag (server not connected in the app)");

        // Not connected: attach straight to window 'one' of session ta.
        client = new PtySession();
        var screen = new StringBuilder();
        client.Output += s => { lock (screen) screen.Append(s); };
        string Screen() { lock (screen) return System.Text.RegularExpressions.Regex.Replace(screen.ToString(), "\x1b\\[[0-9;?]*[A-Za-z]", "").Replace("\r", "").Replace("\x1b", "").TrimEnd(); }
        client.Start(ssh, SshCommand.TmuxAttachArguments(profile, taId, one), 120, 30);
        await Eventually(async () => (await ClientsOf("tmgr-ta")) == "tmgr-ta " + one, "attach did not land in window 'one'");
        checks++; Console.WriteLine("PASS Real tmux: connecting attaches directly to the chosen session and window");

        // Connected: switch the own client to a window of another session through prefix + command prompt.
        await TmuxClient.SwitchToWindowAsync(client, tbId, beta);
        diagnose = async () => "clients: " + await Remote("tmux list-clients -F '#{client_session} #{window_id} #{client_prefix} #{client_key_table}'") + " screen tail: " + System.Text.Json.JsonSerializer.Serialize(Screen()[^Math.Min(Screen().Length, 400)..]) + " raw length " + screen.Length;
        await Eventually(async () => (await ClientsOf("tmgr-tb")) == "tmgr-tb " + beta, "switch-client did not reach session tb / window beta");
        Check((await Remote("tmux has-session -t tmgr-ta && echo alive")).Contains("alive"), "Previous session was closed");
        checks++; Console.WriteLine("PASS Real tmux: switching to a window of another session works and the old session keeps running");

        await TmuxClient.SwitchToWindowAsync(client, taId, ta.Single(w => w.Name == "two").WindowId);
        await Eventually(async () => (await ClientsOf("tmgr-ta")).StartsWith("tmgr-ta " + ta.Single(w => w.Name == "two").WindowId), "switch back into program window failed");
        await TmuxClient.SwitchToWindowAsync(client, tbId, alpha);
        await Eventually(async () => (await ClientsOf("tmgr-tb")) == "tmgr-tb " + alpha, "switch away from the cat window failed");
        string pane = await Remote($"tmux capture-pane -p -t '{taId}:{ta.Single(w => w.Name == "two").WindowId}'");
        Check(!pane.Contains("switch-client") && !pane.Contains(":switch"), "Keystrokes leaked into the running program: " + pane);
        checks++; Console.WriteLine("PASS Real tmux: no characters reach the program running in the window");
    }
    finally
    {
        if (client is not null) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Remote("tmux kill-session -t tmgr-ta 2>/dev/null; tmux kill-session -t tmgr-tb 2>/dev/null; true");
    }
    return checks;
}
