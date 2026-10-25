using System.Reflection;
using System.Text;
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
    }
    catch (Exception ex) { failures.Add("SSH smoke: " + ex.Message); Console.WriteLine("FAIL " + ex.Message); }
    finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
}
Console.WriteLine($"{passed} passed, {failures.Count} failed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
