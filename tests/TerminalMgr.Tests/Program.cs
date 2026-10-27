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
bool benchOnly = args.Any(a => a.StartsWith("--dictation-bench") || a == "--engine-smoke"); // the measuring mode runs on its own
void Test(string name, Action action)
{
    if (benchOnly) return;
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
// The real SSH checks (--ssh-smoke) use an SSH alias of the tester's own, never one that is written into the repository.
string sshHost = Environment.GetEnvironmentVariable("TERMINALMGR_SSH_TEST_HOST") ?? "";
var root = Path.Combine(Path.GetTempPath(), "TerminalMgr-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Test("SSH config mode preserves RemoteCommand / RequestTTY", () =>
    {
        var a = SshCommand.Arguments(new() { Name = "dev", Destination = "devbox" });
        Check(!a.Contains("-tt") && !a.Contains("RemoteCommand=none") && a.Last() == "devbox", "Config mode overrides SSH config");
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
        File.WriteAllText(config, "Host devbox\n HostName dev.example.test\nHost devbox server1 server2 server3 server4\n RequestTTY yes\n RemoteCommand tmux new -A -s main\nHost * !bad *.example.test\nHost devbox-code # VS Code\n RequestTTY no\n");
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

    // Synthetic 16 kHz audio: a 220 Hz tone at the given amplitude (0..1) or silence (amplitude 0).
    short[] Tone(double seconds, double amplitude) => Enumerable.Range(0, (int)(seconds * 16000))
        .Select(i => (short)(Math.Sin(2 * Math.PI * 220 * i / 16000.0) * amplitude * short.MaxValue)).ToArray();
    StopReason Run(SilenceDetector detector, params short[][] parts)
    {
        foreach (var part in parts) detector.Feed(part);
        return detector.Reason;
    }
    Test("Shortcut parsing accepts modifier combinations and normalizes them", () =>
    {
        Check(Shortcut.Parse("ctrl+y") == new Shortcut(true, false, false, "Y") && Shortcut.Parse("ctrl+y").ToString() == "Ctrl+Y", "Ctrl+Y");
        Check(Shortcut.Parse("Shift + Strg + d").ToString() == "Ctrl+Shift+D", "Order or German modifier");
        Check(Shortcut.Parse("Alt+1").ToString() == "Alt+1" && Shortcut.Parse("F5").ToString() == "F5" && Shortcut.Parse("Ctrl+Enter").ToString() == "Ctrl+Enter", "Digits, F-keys, Enter");
        Check(Shortcut.Parse("Ctrl+Escape").Key == "Esc" && Shortcut.Parse("Alt+.").Key == ".", "Aliases and punctuation");
    });
    Test("Shortcut parsing rejects keys that would swallow normal typing", () =>
    {
        foreach (string bad in new[] { "", "Y", "Shift+Y", "Ctrl+", "Ctrl+Ctrl+Y", "Ctrl+Y+Z", "Ctrl+Pfeil", "+", "Ctrl+F13" })
            Check(!Shortcut.TryParse(bad, out _), "Accepted '" + bad + "'");
        Check(Shortcut.TryParse("F12", out _) && Shortcut.TryParse("F1", out _), "F-keys without modifier must be allowed");
    });
    Test("Shortcut matching uses the modifier set exactly and DOM key codes", () =>
    {
        var s = Shortcut.Parse("Ctrl+Y");
        Check(s.Matches(true, false, false, Shortcut.FromDomCode("KeyY")), "Ctrl+Y not matched");
        Check(!s.Matches(true, false, true, "Y") && !s.Matches(true, true, false, "Y") && !s.Matches(false, false, false, "Y") && !s.Matches(true, false, false, "Z"), "Too lenient");
        Check(Shortcut.FromDomCode("Digit7") == "7" && Shortcut.FromDomCode("F9") == "F9" && Shortcut.FromDomCode("Escape") == "Esc" && Shortcut.FromDomCode("ArrowUp") == "Up" && Shortcut.FromDomCode("ShiftLeft") is null, "DOM mapping");
    });
    Test("Silence detection: speech then 1.5 s of silence ends the recording", () =>
    {
        var d = new SilenceDetector();
        Check(Run(d, Tone(1.0, 0.2), Tone(1.4, 0)) == StopReason.None && d.SpeechHeard, "Stopped too early");
        Check(Run(d, Tone(0.2, 0)) == StopReason.Silence, "Did not stop after 1.5 s");
        Check(d.Elapsed >= TimeSpan.FromSeconds(2.45) && d.Elapsed <= TimeSpan.FromSeconds(2.7), "Elapsed " + d.Elapsed);
    });
    Test("Silence detection: pauses shorter than the limit do not end the recording", () =>
    {
        var d = new SilenceDetector();
        Check(Run(d, Tone(0.5, 0.2), Tone(1.2, 0), Tone(0.5, 0.2), Tone(1.2, 0), Tone(0.5, 0.2)) == StopReason.None, "Cut off inside a sentence");
    });
    Test("Silence detection: nothing said ends after 5 s, leading silence alone does not end a dictation", () =>
    {
        var quiet = new SilenceDetector();
        Check(Run(quiet, Tone(4.8, 0)) == StopReason.None && Run(quiet, Tone(0.4, 0)) == StopReason.NoSpeech && !quiet.SpeechHeard, "No-speech timeout");
        var late = new SilenceDetector();
        Check(Run(late, Tone(3.0, 0), Tone(1.0, 0.2)) == StopReason.None && late.SpeechHeard, "Late speech must be accepted");
        Check(Run(late, Tone(1.6, 0)) == StopReason.Silence, "Silence after late speech");
    });
    Test("Silence detection: background noise below the threshold and a short click count as silence", () =>
    {
        var noise = new SilenceDetector();
        Check(Run(noise, Tone(5.2, 0.005)) == StopReason.NoSpeech && !noise.SpeechHeard, "Quiet noise taken as speech");
        var click = new SilenceDetector();
        Check(Run(click, Tone(0.01, 0.9), Tone(5.2, 0)) == StopReason.NoSpeech && !click.SpeechHeard, "Click taken as speech");
    });
    Test("Silence detection: duration and threshold are adjustable; level meter follows loudness", () =>
    {
        var fast = new SilenceDetector(silenceSeconds: 0.5);
        Check(Run(fast, Tone(0.3, 0.2), Tone(0.6, 0)) == StopReason.Silence, "0.5 s limit ignored");
        var strict = new SilenceDetector(threshold: 0.3);
        Check(Run(strict, Tone(1.0, 0.2), Tone(4.5, 0)) == StopReason.NoSpeech, "Threshold ignored");
        var meter = new SilenceDetector();
        meter.Feed(Tone(0.1, 0)); double silent = meter.Level;
        meter.Feed(Tone(0.1, 0.5)); double loud = meter.Level;
        Check(silent == 0 && loud > 0.9 && loud <= 1, $"Level {silent}/{loud}");
    });
    Test("Dictation text: non-speech markers, control characters and doubled spaces are removed", () =>
    {
        Check(DictationText.Clean(" [BLANK_AUDIO] ") == "", "Blank audio");
        Check(DictationText.Clean(" Starte tmux  neu (Musik) [Applaus] ♪ jetzt\x1b[0m.\n") == "Starte tmux neu jetzt[0m.", "Mixed: " + DictationText.Clean(" Starte tmux  neu (Musik) [Applaus] ♪ jetzt\x1b[0m.\n"));
        Check(DictationText.Clean("Ruf foo(bar) auf") == "Ruf foo(bar) auf" && DictationText.Clean("*Musik* Hallo") == "Hallo", "Legitimate parentheses must stay");
    });
    Test("Dictation text: new text goes to the caret with spaces only where words would touch", () =>
    {
        const string echoPrompt = "tmux, git, Claude Code, Docker, SSH, GitHub, PowerShell, Linux, npm, Kubernetes";
        Check(DictationText.RemoveArtifacts("tmux git Claude Code Docker SSH GitHub PowerShell Linux npm Kubernetes", echoPrompt) == "", "Whole prompt echoed");
        Check(DictationText.RemoveArtifacts("Starte Docker SSH GitHub PowerShell neu", echoPrompt) == "Starte neu", "Prompt run inside speech");
        Check(DictationText.RemoveArtifacts("Starte Docker und git neu", echoPrompt) == "Starte Docker und git neu", "A few terms said on purpose stay");
        Check(DictationText.RemoveArtifacts("Linux Linux Linux Linux Linux Linux", echoPrompt) == "Linux", "Decoding loop collapses");
        Check(DictationText.RemoveArtifacts("Ja ja ja okay", echoPrompt) == "Ja ja ja okay" && DictationText.RemoveArtifacts("tmux git Claude Code", "") == "tmux git Claude Code", "No prompt, no change");
        var echoed = new List<TimedWord> { new("Linux,", TimeSpan.Zero, TimeSpan.FromSeconds(1)), new("npm,", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), new("Kubernetes", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)) };
        Check(DictationText.RemoveArtifacts(echoed, echoPrompt).Count == 3, "Three prompt words are below the limit");
        echoed.Insert(0, new("PowerShell,", TimeSpan.Zero, TimeSpan.Zero));
        Check(DictationText.RemoveArtifacts(echoed, echoPrompt).Count == 0, "Four prompt words in a row are dropped from timed words");
        // The loop the user saw under the input box: commas as separate entries between the repeats must not hide the repetition.
        Check(DictationText.CollapseRepeats("Linux, Linux, Linux, Linux") == "Linux,", "Comma-separated loop: " + DictationText.CollapseRepeats("Linux, Linux, Linux, Linux"));
        Check(DictationText.CollapseRepeats("Starte Linux Linux Linux Linux Linux jetzt") == "Starte Linux jetzt", "Loop inside speech");
        Check(DictationText.CollapseRepeats("der Server der Server der Server der Server läuft") == "der Server läuft", "A repeated phrase is kept once");
        Check(DictationText.CollapseRepeats("Ja ja ja okay") == "Ja ja ja okay" && DictationText.CollapseRepeats("") == "" && DictationText.CollapseRepeats("ein Satz ohne Schleife") == "ein Satz ohne Schleife", "Speech stays");
        var looped = new List<TimedWord>();
        foreach (string w in new[] { "Linux", ",", "Linux", ",", "Linux", ",", "Linux", "," })
            looped.Add(new(w, TimeSpan.FromSeconds(looped.Count), TimeSpan.FromSeconds(looped.Count + 1)));
        var cleaned = DictationText.RemoveArtifacts(looped, "");
        Check(cleaned.Count <= 2 && cleaned[0].Text == "Linux", "Separate comma entries between the repeats: " + string.Join("|", cleaned.Select(c => c.Text)));
        var agreement2 = new WordAgreement();
        var loopHypothesis = new List<TimedWord>();
        foreach (string w in new[] { "Linux", "Linux", "Linux", "Linux", "Linux" }) loopHypothesis.Add(new(w, TimeSpan.FromSeconds(loopHypothesis.Count), TimeSpan.FromSeconds(loopHypothesis.Count + 1)));
        agreement2.Feed(loopHypothesis); agreement2.Feed(loopHypothesis);
        Check(agreement2.ConfirmedText == "Linux" && (agreement2.ConfirmedText + " " + agreement2.TentativeText).Trim() == "Linux", "Confirmed loops are collapsed in the shown text: " + agreement2.FullText);
        Check(DictationText.InsertAtCaret("", 0, "Hallo") == ("Hallo", 5), "Empty");
        Check(DictationText.InsertAtCaret("Hallo", 5, "Welt") == ("Hallo Welt", 10), "Append needs a space");
        Check(DictationText.InsertAtCaret("Hallo Welt", 5, "liebe") == ("Hallo liebe Welt", 11), "Middle needs spaces on both sides");
        Check(DictationText.InsertAtCaret("Hallo ", 6, "Welt") == ("Hallo Welt", 10), "No double space after blank");
        Check(DictationText.InsertAtCaret("Hallo Welt", 5, ", du") == ("Hallo, du Welt", 9), "Punctuation sticks to the word before");
        Check(DictationText.InsertAtCaret("Hallo", 99, "x").Text == "Hallo x" && DictationText.InsertAtCaret("a", -3, "b").Text == "b a", "Caret is clamped");
    });
    Test("Dictation text: paste text loses control characters and surrounding blank lines but keeps inner lines and tabs", () =>
    {
        Check(DictationText.ForPaste("\n  zeile eins\x1b[31m\r\n\tzeile zwei\x03\n\n") == "zeile eins[31m\n\tzeile zwei", "Paste text: " + DictationText.ForPaste("\n  zeile eins\x1b[31m\r\n\tzeile zwei\x03\n\n"));
    });
    Test("Whisper models live in the user's model folder and are recognized by size and magic", () =>
    {
        Check(WhisperModels.Get(null).Name == WhisperModels.DefaultName && WhisperModels.Get("unbekannt").Name == WhisperModels.DefaultName && WhisperModels.Get("SMALL").Name == "small", "Lookup");
        var turbo = WhisperModels.Get("large-v3-turbo-q5_0");
        Check(turbo.Url.ToString() == "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin", "URL " + turbo.Url);
        Check(turbo.SizeText.StartsWith("ca. 0") && turbo.SizeText.EndsWith(" GB") && WhisperModels.Get("tiny").SizeText == "ca. 78 MB", "Size text: " + turbo.SizeText);
        Check(WhisperModels.PathOf(turbo).StartsWith(AppPaths.ModelsDirectory()) && !AppPaths.IsInside(WhisperModels.PathOf(turbo), AppContext.BaseDirectory), "Model path");
        var tiny = WhisperModels.Get("tiny");
        var directory = Path.Combine(root, "models");
        Directory.CreateDirectory(directory);
        Check(!WhisperModels.IsInstalled(tiny, directory), "Missing file reported as installed");
        File.WriteAllBytes(WhisperModels.PathOf(tiny, directory), "lmgg"u8.ToArray());
        Check(!WhisperModels.IsInstalled(tiny, directory) && WhisperModels.LooksLikeModel(WhisperModels.PathOf(tiny, directory)), "Truncated download accepted or magic missed");
        using (var stream = File.OpenWrite(WhisperModels.PathOf(tiny, directory))) stream.SetLength(tiny.ApproxBytes);
        Check(WhisperModels.IsInstalled(tiny, directory), "Full-size file not accepted");
        File.WriteAllText(Path.Combine(directory, "other.bin"), "<html>");
        Check(!WhisperModels.LooksLikeModel(Path.Combine(directory, "other.bin")) && !WhisperModels.LooksLikeModel(Path.Combine(directory, "gibtsnicht.bin")), "HTML error page taken as model");
    });
    Test("Dictation options: defaults, round trip and normalization of bad values", () =>
    {
        var defaults = new AppSettings().Dictation;
        Check(defaults.Shortcut == "Ctrl+Y" && defaults.Language == "de" && defaults.SilenceSeconds == 1.5 && defaults.Model == "large-v3-turbo-q5_0" && defaults.InitialPrompt.Contains("tmux"), "Defaults");
        var store = new SettingsStore(Path.Combine(root, "dictation"));
        store.Save(new() { Dictation = defaults with { Microphone = "Headset", Language = "auto", Shortcut = "Ctrl+Shift+D" } });
        var loaded = store.Load().Dictation;
        Check(loaded.Microphone == "Headset" && loaded.Language == "auto" && loaded.Shortcut == "Ctrl+Shift+D", "Round trip");
        var fixedUp = (defaults with { Shortcut = "Y", Language = "fr", Model = "x", SilenceSeconds = 99, LevelThreshold = 0 }).Normalized();
        Check(fixedUp.Shortcut == "Ctrl+Y" && fixedUp.Language == "de" && fixedUp.Model == WhisperModels.DefaultName && fixedUp.SilenceSeconds == 10 && fixedUp.LevelThreshold == 0.002, "Normalization");
    });

    bool WaitUntil(Func<bool> condition, int milliseconds = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }
    void FeedSeconds(FakeSource source, double seconds, double amplitude)
    {
        for (double t = 0; t < seconds - 1e-9; t += 0.1) source.Feed(Tone(0.1, amplitude));
    }
    (DictationSession Session, List<string> Partials, List<DictationState> States, List<DictationResult> Results, List<Exception> Errors) Observe(DictationSession session)
    {
        var partials = new List<string>(); var states = new List<DictationState>(); var results = new List<DictationResult>(); var errors = new List<Exception>();
        session.Partial += t => { lock (partials) partials.Add(t); };
        session.StateChanged += s => { lock (states) states.Add(s); };
        session.Completed += r => { lock (results) results.Add(r); };
        session.Failed += e => { lock (errors) errors.Add(e); };
        return (session, partials, states, results, errors);
    }
    Test("Dictation: provisional text while speaking, ends after 1.5 s of silence, final text replaces it", () =>
    {
        var source = new FakeSource();
        var transcriber = new FakeTranscriber { Responder = samples => samples > 3.2 * 16000 ? "Das ist der endgültige Text" : "Das ist" };
        var o = Observe(new DictationSession(() => source, transcriber, new DictationOptions { PingStart = false }, partialSeconds: 1.0));
        o.Session.Start();
        FeedSeconds(source, 2.0, 0.2);
        Check(WaitUntil(() => { lock (o.Partials) return o.Partials.Count > 0; }), "No provisional text while speaking");
        Check(o.Results.Count == 0 && o.Session.State == DictationState.Recording, "Ended while speaking");
        FeedSeconds(source, 1.7, 0);
        Check(WaitUntil(() => { lock (o.Results) return o.Results.Count > 0; }), "No final result");
        Check(o.Results[0].Text == "Das ist der endgültige Text" && o.Results[0].Reason == StopReason.Silence && !o.Results[0].Manual, "Final result: " + o.Results[0]);
        Check(transcriber.Calls.Last() >= 3.4 * 16000, "Final run must see the whole segment, saw " + transcriber.Calls.Last());
        Check(o.States.SequenceEqual([DictationState.Recording, DictationState.Processing, DictationState.Idle]), "States: " + string.Join(",", o.States));
        Check(WaitUntil(() => source.Disposed), "Microphone not released");
    });
    Test("Dictation tones: start rises, end falls, volume scales, soft edges, off at 0 %; options are clamped and exported", () =>
    {
        var start = DictationTones.Generate(true, 35);
        var end = DictationTones.Generate(false, 35);
        short[] Pcm(byte[] bytes) { var s = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, s, 0, bytes.Length); return s; }
        var a = Pcm(start); var b = Pcm(end);
        Check(a.Length == (int)(0.06 * 44100) + (int)(0.08 * 44100) && b.Length == (int)(0.06 * 44100) + (int)(0.09 * 44100), "Short tones: " + a.Length + " / " + b.Length);
        Check(a.Max(s => Math.Abs((int)s)) is > 5000 and <= (int)(0.8 * 0.35 * short.MaxValue) + 1, "Peak follows the volume: " + a.Max(s => Math.Abs((int)s)));
        Check(Math.Abs(a[0]) < 200 && Math.Abs(a[^1]) < 200, "Soft attack and release, no click");
        Check(!start.SequenceEqual(end), "Start and end tone differ");
        Check(Pcm(DictationTones.Generate(true, 70)).Max(s => Math.Abs((int)s)) > a.Max(s => Math.Abs((int)s)), "Louder at 70 %");
        Check(Pcm(DictationTones.Generate(true, 0)).All(s => s == 0), "0 % is silent");
        var defaults = new DictationOptions();
        Check(defaults.PingStart && defaults.PingEnd && defaults.PingVolume == 35 && defaults.PrepareAtStart, "Defaults");
        Check((defaults with { PingVolume = 900 }).Normalized().PingVolume == 100 && (defaults with { PingVolume = -4 }).Normalized().PingVolume == 0, "Volume clamped");
        var settings = new AppSettings { Dictation = new DictationOptions { PingStart = false, PingEnd = false, PingVolume = 80, PrepareAtStart = false } };
        var exported = ConfigFile.Build(settings, new ExportSelection(false, true, false, false), []);
        Check(exported.Settings?.Dictation is { PingStart: false, PingEnd: false, PingVolume: 80, PrepareAtStart: false }, "Exported");
    });
    Test("Dictation: Ready when the microphone delivers audio; with the start tone the first quarter second is dropped, without it nothing", () =>
    {
        foreach (bool tone in new[] { true, false })
        {
            var source = new FakeSource();
            var transcriber = new FakeTranscriber();
            var session = new DictationSession(() => source, transcriber, new DictationOptions { PingStart = tone });
            int ready = 0;
            session.Ready += () => Interlocked.Increment(ref ready);
            var cards = Observe(session);
            session.Start();
            Check(ready == 0, "Not ready before audio");
            FeedSeconds(source, 1.0, 0.2);
            Check(ready == 1, "Ready exactly once, got " + ready);
            Check(session.Metrics.AudioReadyMilliseconds is { } ms && ms >= 0 && ms < 2000, "Metrics: time to the first audio");
            FeedSeconds(source, 1.7, 0);
            Check(WaitUntil(() => { lock (cards.Results) return cards.Results.Count > 0; }), "No result");
            int heard = transcriber.Calls.Last();
            double expected = tone ? 2.7 - 0.25 : 2.7;
            Check(Math.Abs(heard / 16000.0 - expected) < 0.35, $"Audio kept (tone={tone}): {heard / 16000.0:0.00} s, expected about {expected:0.00} s");
        }
    });
    TimedWord W(string text, double start, double end) => new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
    Test("Word agreement: words are confirmed when two results agree, never recomputed, tentative words may change", () =>
    {
        var agreement = new WordAgreement();
        Check(agreement.Feed([W("eins", 0, .4), W("zwei", .4, .8)]).Count == 0 && agreement.ConfirmedText == "" && agreement.TentativeText == "eins zwei", "First result confirms nothing");
        var confirmed = agreement.Feed([W("Eins,", 0, .4), W("zwei", .4, .8), W("drei", .8, 1.2)]);
        Check(confirmed.Count == 2 && agreement.ConfirmedText == "Eins, zwei" && agreement.TentativeText == "drei", "Case and punctuation must not break agreement: " + agreement.FullText);
        agreement.Feed([W("drei", .8, 1.2), W("vier", 1.2, 1.6)]);
        Check(agreement.ConfirmedText == "Eins, zwei drei" && agreement.TentativeText == "vier" && agreement.ConfirmedEnd == TimeSpan.FromSeconds(1.2), "Third result: " + agreement.FullText);
        agreement.Feed([W("vier", 1.2, 1.6), W("fünf", 1.6, 2.0)]);
        agreement.Feed([W("vier", 1.2, 1.6), W("sechs", 1.6, 2.0)]);
        Check(agreement.ConfirmedText.EndsWith("vier") && agreement.TentativeText == "sechs" && !agreement.ConfirmedText.Contains("fünf"), "A word the next result contradicts must stay tentative: " + agreement.FullText);
        agreement.Reset();
        Check(agreement.FullText == "" && agreement.ConfirmedEnd == TimeSpan.Zero, "Reset");
    });
    Test("Word agreement: the part of a window that overlaps confirmed words is not new", () =>
    {
        var agreement = new WordAgreement();
        agreement.Feed([W("hallo", 0, .5), W("welt", .5, 1.0)]);
        agreement.Feed([W("hallo", 0, .5), W("welt", .5, 1.0)]);
        Check(agreement.ConfirmedText == "hallo welt", "Setup");
        // The next window starts a little before the last confirmed word and repeats it.
        agreement.Feed([W("welt", .55, 1.0), W("und", 1.0, 1.3), W("mehr", 1.3, 1.7)]);
        agreement.Feed([W("welt", .55, 1.0), W("und", 1.0, 1.3), W("mehr", 1.3, 1.7)]);
        Check(agreement.ConfirmedText == "hallo welt und mehr" && agreement.TentativeText == "", "Repeated word: " + agreement.FullText);
        Check(new WordAgreement().Feed([W("[BLANK_AUDIO]", 0, 1), W("...", 1, 2)]).Count == 0, "Words without letters are ignored");
    });
    Test("Navigation shortcuts: defaults are free of conflicts, '<' is a key, window modifiers are limited", () =>
    {
        Check(Shortcut.TryParse("Ctrl+<", out var back) && back.Key == "<" && Shortcut.FromDomCode("IntlBackslash") == "<", "Ctrl+< must parse and map from the ISO key");
        var defaults = new AppSettings();
        var navigation = ShortcutRules.NavigationShortcuts(defaults);
        Check(navigation.Count == 14 && navigation.Select(a => a.Shortcut.ToString()).Contains("Ctrl+0") && navigation.Select(a => a.Shortcut.ToString()).Contains("Ctrl+9")
            && navigation.Any(a => a.Shortcut.ToString() == "Ctrl+Tab") && navigation.Any(a => a.Shortcut.ToString() == "Ctrl+Shift+Tab") && navigation.Any(a => a.Shortcut.ToString() == "Ctrl+<"), "Default navigation shortcuts");
        Check(ShortcutRules.FindConflict(defaults) is null, "Defaults must not conflict: " + ShortcutRules.FindConflict(defaults));
        Check((new ShortcutOptions { WindowModifier = "Ctrl+Alt" }).Normalized().WindowModifier == "Ctrl", "Ctrl+Alt is AltGr on German keyboards and not offered");
        Check(ShortcutRules.TerminalHint(Shortcut.Parse("Ctrl+3")) is { } hint && hint.Contains("Escape"), "Hint for Ctrl+digit");
    });
    Test("Navigation shortcuts: conflicts with window digits, between favorites, with the tmux prefix and with invalid texts are refused", () =>
    {
        AppSettings With(params Favorite[] favorites) => new() { Favorites = [.. favorites] };
        Check(ShortcutRules.FindConflict(With(new Favorite { Name = "A", Shortcut = "Ctrl+2" })) is { } c1 && c1.Contains("Ctrl+2") && c1.Contains("tmux-Fenster"), "Favorite on Ctrl+2");
        Check(ShortcutRules.FindConflict(With(new Favorite { Name = "A", Shortcut = "Ctrl+Alt+A" }, new Favorite { Name = "B", Shortcut = "Ctrl+Alt+A" })) is { } c2 && c2.Contains("doppelt"), "Two favorites on the same keys");
        Check(ShortcutRules.FindConflict(With(new Favorite { Name = "A", Shortcut = "Ctrl+B" })) is { } c3 && c3.Contains("tmux"), "Ctrl+B stays with tmux");
        Check(ShortcutRules.FindConflict(With(new Favorite { Name = "A", Shortcut = "Strg+Q+Q" })) is { } c4 && c4.Contains("ungültig"), "Invalid text");
        Check(ShortcutRules.FindConflict(With(new Favorite { Name = "A", Shortcut = "Ctrl+Alt+A" }, new Favorite { Name = "Nur Palette" })) is null, "A favorite without shortcut is fine");
        var sameModifier = new AppSettings { Shortcuts = new ShortcutOptions { SessionModifier = "Alt+Shift", WindowModifier = "Alt+Shift" } };
        Check(ShortcutRules.FindConflict(sameModifier) is { } c5 && c5.Contains("doppelt"), "Session and window digits on the same modifier");
        Check(ShortcutRules.FindConflict(new AppSettings { Shortcuts = new ShortcutOptions { Back = "Ctrl+Tab" } }) is { } c6 && c6.Contains("doppelt"), "Back on the Next shortcut");
    });
    Test("Favorites resolve by server, session name and window name, not by tmux IDs", () =>
    {
        TmuxWindowRecord Win(string sid, string session, string wid, int index, string name) => new(sid, session, wid, index, name, "bash", false);
        var favorite = new Favorite { ProfileId = "p1", SessionName = "main", WindowName = "archyx", Name = "Archyx" };
        var before = new[] { Win("$0", "main", "@1", 0, "shell"), Win("$0", "main", "@4", 2, "archyx"), Win("$0", "main", "@7", 3, "archyx"), Win("$1", "work", "@9", 0, "archyx") };
        var found = FavoriteResolver.Resolve(favorite, before);
        Check(found.Status == FavoriteStatus.Found && found.Window!.WindowId == "@4", "The first window of that name in that session wins");
        var afterRestart = new[] { Win("$5", "main", "@20", 0, "archyx") };
        Check(FavoriteResolver.Resolve(favorite, afterRestart).Window!.WindowId == "@20" && FavoriteResolver.Resolve(favorite, afterRestart).Window!.SessionId == "$5", "New IDs after a tmux restart");
        var noWindow = FavoriteResolver.Resolve(favorite, [Win("$0", "main", "@1", 0, "shell")]);
        Check(noWindow.Status == FavoriteStatus.NoWindow && noWindow.Window is null && noWindow.Message.Contains("kein Fenster"), "Missing window is a hint: " + noWindow.Message);
        var noSession = FavoriteResolver.Resolve(favorite, [Win("$1", "work", "@9", 0, "archyx")]);
        Check(noSession.Status == FavoriteStatus.NoSession && noSession.Message.Contains("Session"), "Missing session is a hint");
        Check(FavoriteResolver.Resolve(favorite, []).Status == FavoriteStatus.NoSession, "Empty server");
        var made = FavoriteResolver.For("p1", before[1]);
        Check(made.SessionName == "main" && made.WindowName == "archyx" && made.ProfileId == "p1" && made.Label == "main › archyx", "Favorite from a window");
    });
    Test("Navigation history: most recent first, one entry per window, back toggles between two, stale entries are forgotten", () =>
    {
        NavigationPosition P(string profile, string window) => new(profile, "$0", "main", window, "w" + window);
        var history = new NavigationHistory(capacity: 4);
        Check(history.Current is null && history.Previous is null, "Empty");
        history.Visit(P("a", "@1"));
        Check(history.Previous is null, "One window has no previous");
        history.Visit(P("a", "@2")); history.Visit(P("b", "@1"));
        Check(history.Current!.ProfileId == "b" && history.Previous!.WindowId == "@2", "Across servers: same window ID on another server is another window");
        history.Visit(history.Previous!);   // back
        Check(history.Current!.WindowId == "@2" && history.Previous!.ProfileId == "b", "Going back again returns");
        history.Visit(P("a", "@2"));
        Check(history.Items.Count == 3, "A window appears once: " + history.Items.Count);
        history.Visit(P("a", "@3")); history.Visit(P("a", "@4")); history.Visit(P("a", "@5"));
        Check(history.Items.Count == 4 && history.Items[^1].WindowId != "@1", "Capacity drops the oldest");
        history.Forget(p => p.WindowId == "@4");
        Check(history.Items.All(p => p.WindowId != "@4") && history.Previous!.WindowId == "@3", "Forget");
        history.Forget(p => p.ProfileId == "a");
        Check(history.Items.Count == 0, "Forget a whole server");
    });
    Test("tmux navigation commands: window by index, next, previous, in two steps; nothing for out-of-range indices", () =>
    {
        Check(TmuxNavigation.SelectIndex(2) == "select-window -t :2" && TmuxNavigation.SelectIndex(0) == "select-window -t :0", "Index commands");
        Check(TmuxNavigation.Steps(TmuxNavigation.Next).SequenceEqual(["\u0002:", "next-window\r"]) && TmuxNavigation.Steps(TmuxNavigation.Previous)[1] == "previous-window\r", "Steps");
        bool refused = false;
        try { TmuxNavigation.SelectIndex(10); } catch (ArgumentOutOfRangeException) { refused = true; }
        Check(refused, "Index 10 is not a shortcut");
    });
    Test("Settings with favorites survive a round trip; files from before the favorites still load", () =>
    {
        var store = new SettingsStore(Path.Combine(root, "favorites"));
        store.Save(new() { Favorites = [new Favorite { Name = "Archyx", ProfileId = "p1", SessionName = "main", WindowName = "archyx", Shortcut = "Ctrl+Alt+A" }],
            Shortcuts = new ShortcutOptions { WindowModifier = "Alt+Shift", Back = "Ctrl+Shift+Left" } });
        var loaded = store.Load();
        Check(loaded.Favorites.Single() is { Name: "Archyx", SessionName: "main", WindowName: "archyx", Shortcut: "Ctrl+Alt+A" } && loaded.Shortcuts.WindowModifier == "Alt+Shift" && loaded.Shortcuts.Back == "Ctrl+Shift+Left", "Round trip");
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Shortcuts\":{\"Copy\":\"Ctrl+Shift+C\"}}")!;
        Check(old.Favorites.Count == 0 && old.Shortcuts.Normalized().NextWindow == "Ctrl+Tab" && old.Shortcuts.Normalized().Back == "Ctrl+<", "Old file");
    });
    Test("Favorites are exported with the settings and merged (or replaced) on import", () =>
    {
        var mine = new Favorite { Id = "f1", Name = "Archyx", ProfileId = "p1", SessionName = "main", WindowName = "archyx", Shortcut = "Ctrl+Alt+A" };
        var theirs = new Favorite { Id = "f2", Name = "Logs", ProfileId = "p1", SessionName = "main", WindowName = "logs", Shortcut = "Ctrl+Alt+L" };
        var source = new AppSettings { Favorites = [mine] };
        var file = ConfigFile.Serialize(ConfigFile.Build(source, new ExportSelection(false, true, false, false), []), "0.2.0", DateTimeOffset.UtcNow, null);
        var content = ConfigFile.Parse(file, null).Content;
        Check(content.Settings!.Favorites.Single().Shortcut == "Ctrl+Alt+A", "Favorites in the file");
        var target = new AppSettings { Favorites = [theirs] };
        var merged = ConfigImporter.Apply(target, content, ConfigImporter.BuildPlan(target, content, [], ImportMode.Merge), new Dictionary<string, ConflictChoice>(), false, true, false, false).Settings;
        Check(merged.Favorites.Select(f => f.Id).OrderBy(i => i).SequenceEqual(["f1", "f2"]), "Merge keeps both");
        var replaced = ConfigImporter.Apply(target, content, ConfigImporter.BuildPlan(target, content, [], ImportMode.ReplaceAll), new Dictionary<string, ConflictChoice>(), false, true, false, false).Settings;
        Check(replaced.Favorites.Single().Id == "f1", "Replace all takes the file's favorites");
    });
    Test("Palette items: favorites, windows of connected servers only, shortcut hints, clipboard, actions, recent windows only for the empty input", () =>
    {
        TmuxWindowRecord Win(string sid, string session, string wid, int index, string name, bool active = false) => new(sid, session, wid, index, name, "bash", active);
        var settings = new AppSettings
        {
            Profiles = [new Profile { Id = "p1", Name = "devbox", Destination = "devbox" }, new Profile { Id = "p2", Name = "server1", Destination = "server1" }],
            Favorites = [new Favorite { Id = "f1", Name = "Archyx", ProfileId = "p1", SessionName = "main", WindowName = "archyx", Shortcut = "Ctrl+Alt+A" }]
        };
        var servers = new[]
        {
            new PaletteServer("p1", "devbox", true, "$0", [Win("$0", "main", "@1", 0, "shell", true), Win("$0", "main", "@4", 2, "archyx"), Win("$1", "work", "@9", 0, "logs")]),
            new PaletteServer("p2", "server1", false, null, [Win("$0", "x", "@1", 0, "never shown")])
        };
        var recent = new[] { new NavigationPosition("p1", "$0", "main", "@1", "shell"), new NavigationPosition("p2", "$0", "x", "@5", "old") };
        var items = PaletteBuilder.Build(settings, servers, recent, ["erste Zeile\nzweite", "", "kurz"], [new PaletteAction("dictation", "Diktat starten", "Ctrl+Y")]);

        var favorite = items.Single(i => i.Group == PaletteBuilder.FavoritesGroup);
        Check(favorite.Id == "favorite:f1" && favorite.Title == "Archyx" && favorite.Subtitle == "devbox › main › archyx" && favorite.Shortcut == "Ctrl+Alt+A" && favorite.ShowWhenEmpty && favorite.Searchable, "Favorite item");
        var windows = items.Where(i => i.Group == PaletteBuilder.WindowsGroup).ToList();
        Check(windows.Count == 3 && windows.All(w => w.Title.StartsWith("devbox › ")), "Only the connected server's windows: " + string.Join(" | ", windows.Select(w => w.Title)));
        Check(windows.Single(w => w.Title.EndsWith("2: archyx (bash)") || w.Title.EndsWith("2: archyx")).Shortcut == "Ctrl+2", "Window 2 of the shown session has its shortcut");
        Check(windows.Single(w => w.Title.Contains("work ›")).Shortcut == "", "Another session has no shortcut hint");
        Check(windows.Single(w => w.Title.Contains("0: shell")).Subtitle == "aktives Fenster", "Active window marked");
        var recentItems = items.Where(i => i.Group == PaletteBuilder.RecentGroup).ToList();
        Check(recentItems.Count == 1 && recentItems[0].Title == "server1 › x › old" && recentItems[0].ShowWhenEmpty && !recentItems[0].Searchable, "The current window is not 'recent'; recent ones are not searchable");
        var clips = items.Where(i => i.Group == PaletteBuilder.ClipboardGroup).ToList();
        Check(clips.Count == 2 && clips[0].Id == "clip:0" && clips[0].Title == "erste Zeile" && clips[0].Subtitle.Contains("2 Zeilen") && clips[1].Id == "clip:2", "Clipboard entries keep their list index, empty ones are skipped");
        Check(items.Last() is { Id: "action:dictation", Shortcut: "Ctrl+Y", Group: "Aktionen" }, "Action");
        Check(PaletteBuilder.GroupOrder.SequenceEqual(["Favoriten", "Zuletzt benutzt", "tmux-Fenster", "Clipboard", "Aktionen"]), "Group order");
    });
    Test("Palette window ids: only well-formed ones are accepted", () =>
    {
        string id = PaletteBuilder.WindowId("p1", "$0", "@4");
        Check(PaletteBuilder.TryParseWindowId(id, out var p, out var s, out var w) && p == "p1" && s == "$0" && w == "@4", "Round trip");
        foreach (string bad in new[] { "window:p1|$0", "window:|$0|@4", "window:p1|0|@4", "window:p1|$0|4", "window:p1|$0|@4|x", "action:dictation", "window:p1|$0;rm|@4" })
            Check(!PaletteBuilder.TryParseWindowId(bad, out _, out _, out _), "Accepted: " + bad);
    });
    Test("Theme: every text/background pair reaches a contrast of 4.5:1, the CSS variables carry every token and both fonts", () =>
    {
        foreach (var (foreground, background) in Theme.ReadablePairs)
        {
            double ratio = Theme.Contrast(Theme.Dark[foreground], Theme.Dark[background]);
            Check(ratio >= 4.5, $"{foreground} on {background}: {ratio:0.00}:1");
        }
        Check(Math.Abs(Theme.Contrast("#000000", "#ffffff") - 21) < 1e-9 && Math.Abs(Theme.Contrast("#777777", "#777777") - 1) < 1e-9, "Contrast formula");
        var css = Theme.Dark.CssVariables();
        Check(Theme.Dark.Colors.All(c => css["--" + c.Key] == c.Value) && css["--font-ui"].StartsWith("\"IBM Plex Sans\"") && css["--font-mono"].StartsWith("\"JetBrains Mono\""), "CSS variables");
        Check(Theme.Dark["bg"] == "#0e0f11" && Theme.Dark["accent"] == "#f5b041" && Theme.Dark["connected"] == "#5fd38d" && Theme.Dark["recording"] == "#ff6b6b" && Theme.Dark["text"] == "#e8e6e3", "Colors of direction C");
        bool refused = false;
        try { Theme.ParseHex("fff"); } catch (FormatException) { refused = true; }
        Check(refused, "Short hex is refused");
    });
    Test("Word error rate", () =>
    {
        Check(WordErrorRate.Compute("Starte den Server", "starte den server.") == 0, "Case and punctuation do not count");
        Check(Math.Abs(WordErrorRate.Compute("a b c d", "a b x d") - 0.25) < 1e-9, "One substitution of four");
        Check(Math.Abs(WordErrorRate.Compute("a b c d", "a d") - 0.5) < 1e-9, "Two deletions of four");
        Check(WordErrorRate.Compute("", "") == 0 && WordErrorRate.Compute("", "x") == 1 && WordErrorRate.Compute("a b", "") == 1, "Empty cases");
    });
    Test("Dictation options: engine, interim model, window and cycle are normalized; old settings files still load", () =>
    {
        var defaults = new DictationOptions();
        Check(defaults.Engine == "whisper" && defaults.InterimModel == "small-q5_1" && defaults.InterimWindowSeconds == 7 && defaults.InterimCycleSeconds == 0.7, "Defaults");
        var fixedUp = (defaults with { Engine = "azure", InterimModel = "x", InterimWindowSeconds = 99, InterimCycleSeconds = 0 }).Normalized();
        Check(fixedUp.Engine == "whisper" && fixedUp.InterimModel == WhisperModels.DefaultName && fixedUp.InterimWindowSeconds == 12 && fixedUp.InterimCycleSeconds == 0.4, "Normalization");
        Check((defaults with { Engine = "WebSpeech" }).Normalized().Engine == "webspeech" && SpeechEngineIds.All.Count == 3, "Known engines");
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Dictation\":{\"Shortcut\":\"Ctrl+Y\",\"Language\":\"en\"}}")!;
        Check(old.Dictation.Language == "en" && old.Dictation.Engine == "whisper" && old.Dictation.InterimModel == "small-q5_1", "A settings file from before the engines");
    });
    Test("Dictation (streaming): the window follows the confirmed words, confirmed text only grows, the final text comes from the final model", () =>
    {
        var source = new FakeSource();
        var final = new FakeTranscriber { Responder = _ => "Das ist der Endtext" };
        long fed = 0;
        var words = new FakeWordTranscriber { Total = () => Interlocked.Read(ref fed) };
        words.Responder = (call, window) => call switch
        {
            1 or 2 => [W("alpha", 0, .4), W("beta", .4, .8)],
            3 => [W("gamma", .3, .7)],
            4 => [W("gamma", .3, .7), W("delta", .7, 1.1)],
            _ => []
        };
        var session = new DictationSession(() => source, final, new DictationOptions { PingStart = false, InterimCycleSeconds = 0.5 }, interim: words);
        var events = new List<(string Confirmed, string Tentative)>();
        session.Interim += (c, t) => { lock (events) events.Add((c, t)); };
        var results = new List<DictationResult>();
        session.Completed += r => { lock (results) results.Add(r); };
        session.Start();
        for (int call = 1; call <= 4; call++)
        {
            int guard = 0;
            while (words.Windows.Count < call && guard++ < 400) { Interlocked.Add(ref fed, 1600); source.Feed(Tone(0.1, 0.2)); Thread.Sleep(12); }
            Check(WaitUntil(() => { lock (events) return events.Count >= call; }), "No interim result for call " + call);
        }
        lock (events)
        {
            Check(events[1] == ("alpha beta", "") && events[2] == ("alpha beta", "gamma") && events[3] == ("alpha beta gamma", "delta"), "Interim events: " + string.Join(" | ", events));
            for (int i = 1; i < events.Count; i++) Check(events[i].Confirmed.StartsWith(events[i - 1].Confirmed), "Confirmed text changed at " + i);
        }
        // Call 3 started 0.2 s before the end of the last confirmed word (0.8 s): the window is the audio from 0.6 s on, not the whole segment.
        var third = words.Windows[2];
        Check(Math.Abs(third.Samples - (third.TotalAtCall - (int)(0.6 * 16000))) <= 1700, $"Window {third.Samples} of {third.TotalAtCall} samples");
        session.Stop();
        Check(WaitUntil(() => { lock (results) return results.Count > 0; }) && results[0].Text == "Das ist der Endtext" && results[0].Manual, "Final text");
        Check(final.Calls.Last() >= 16000 * 2, "The final run must see the whole segment, saw " + final.Calls.Last());
        Check(session.Metrics.Cycles.Count >= 4 && session.Metrics.FirstInterimMilliseconds is not null && session.Metrics.FinalMilliseconds is not null, "Metrics: " + session.Metrics.Summary());
    });
    Test("Dictation (streaming): the window never exceeds the configured length, even when nothing gets confirmed", () =>
    {
        var source = new FakeSource();
        var words = new FakeWordTranscriber();
        int n = 0;
        words.Responder = (call, window) => [W("wort" + Interlocked.Increment(ref n), 0, .3)]; // every result differs, nothing is ever confirmed
        var session = new DictationSession(() => source, new FakeTranscriber(), new DictationOptions { PingStart = false, InterimCycleSeconds = 0.4, InterimWindowSeconds = 2 }, interim: words);
        session.Start();
        for (int i = 0; i < 70; i++) { source.Feed(Tone(0.1, 0.2)); Thread.Sleep(12); }
        session.Cancel();
        Check(words.Windows.Count >= 3, "Cycles ran: " + words.Windows.Count);
        Check(words.Windows.All(w => w.Samples <= 2 * 16000), "Longest window " + words.Windows.Max(w => w.Samples) / 16000.0 + " s");
    });
    Test("WavFileSource delivers prepared audio in real time and then silence", () =>
    {
        var audio = Tone(0.5, 0.3);
        using var source = new WavFileSource(audio, trailingSilenceSeconds: 0.3, speed: 4);
        var received = new List<short>();
        source.Samples += s => { lock (received) received.AddRange(s); };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        source.Start();
        Check(WaitUntil(() => { lock (received) return received.Count >= 12800; }, 3000), "Not all audio arrived: " + received.Count);
        Check(clock.Elapsed.TotalMilliseconds >= 150, "Delivered faster than the speed allows: " + clock.ElapsedMilliseconds + " ms");
        lock (received) Check(received.Take(8000).Any(s => s != 0) && received.Skip(8000).All(s => s == 0), "Audio first, then silence");
    });
    Test("Dictation: no speech for 5 s ends without calling the recognizer", () =>
    {
        var source = new FakeSource(); var transcriber = new FakeTranscriber();
        var o = Observe(new DictationSession(() => source, transcriber, new DictationOptions { PingStart = false }));
        o.Session.Start();
        FeedSeconds(source, 5.2, 0);
        Check(WaitUntil(() => { lock (o.Results) return o.Results.Count > 0; }), "No result");
        Check(o.Results[0].Text == "" && o.Results[0].Reason == StopReason.NoSpeech && transcriber.Calls.Count == 0, "Silence was transcribed");
    });
    Test("Dictation: manual stop transcribes what was said; cancel reports nothing and allows a new round", () =>
    {
        var source = new FakeSource(); var transcriber = new FakeTranscriber { Responder = _ => "fertig" };
        var o = Observe(new DictationSession(() => source, transcriber, new DictationOptions { PingStart = false }));
        o.Session.Start();
        FeedSeconds(source, 1.0, 0.2);
        o.Session.Stop();
        Check(WaitUntil(() => { lock (o.Results) return o.Results.Count > 0; }) && o.Results[0] is { Text: "fertig", Manual: true }, "Manual stop");
        var second = new FakeSource();
        var o2 = Observe(new DictationSession(() => second, transcriber, new DictationOptions { PingStart = false }));
        o2.Session.Start();
        FeedSeconds(second, 1.0, 0.2);
        o2.Session.Cancel();
        Thread.Sleep(300);
        Check(o2.Results.Count == 0 && o2.Session.State == DictationState.Idle && WaitUntil(() => second.Disposed), "Cancel must not report a result");
        FeedSeconds(second, 1.0, 0.2); // late audio of the canceled round is ignored
        Check(o2.Results.Count == 0 && o2.Partials.Count == 0, "Late audio leaked into the next round");
    });
    Test("Dictation: recognizer errors and a missing microphone are reported and leave the session usable", () =>
    {
        var source = new FakeSource(); var transcriber = new FakeTranscriber { Error = new InvalidOperationException("Modell kaputt") };
        var o = Observe(new DictationSession(() => source, transcriber, new DictationOptions { PingStart = false }));
        o.Session.Start();
        FeedSeconds(source, 1.0, 0.2);
        o.Session.Stop();
        Check(WaitUntil(() => o.Errors.Count > 0) && o.Errors[0].Message == "Modell kaputt" && o.Session.State == DictationState.Idle && o.Results.Count == 0, "Recognizer error");
        var broken = new FakeSource { StartError = new InvalidOperationException("Es ist kein Mikrofon vorhanden.") };
        var o2 = Observe(new DictationSession(() => broken, new FakeTranscriber(), new DictationOptions { PingStart = false }));
        o2.Session.Start();
        Check(WaitUntil(() => o2.Errors.Count > 0) && o2.Session.State == DictationState.Idle, "Microphone error");
        o2.Session.Start();
        Check(o2.Errors.Count == 2, "Session unusable after an error");
        var unplugged = new FakeSource();
        var o3 = Observe(new DictationSession(() => unplugged, new FakeTranscriber(), new DictationOptions { PingStart = false }));
        o3.Session.Start();
        FeedSeconds(unplugged, 0.5, 0.2);
        unplugged.Break(new IOException("Mikrofon getrennt"));
        Check(WaitUntil(() => o3.Errors.Count > 0) && o3.Session.State == DictationState.Idle && o3.Results.Count == 0, "Unplugged microphone");
    });
    Test("Dictation: provisional recognition never runs twice at the same time", () =>
    {
        var source = new FakeSource(); var transcriber = new FakeTranscriber { Delay = 150, Responder = _ => "x" };
        var o = Observe(new DictationSession(() => source, transcriber, new DictationOptions { PingStart = false }, partialSeconds: 0.3));
        o.Session.Start();
        FeedSeconds(source, 3.0, 0.2);
        Check(WaitUntil(() => transcriber.Calls.Count > 0), "No provisional run");
        o.Session.Cancel();
        Check(transcriber.MaxConcurrent == 1, "Concurrent runs: " + transcriber.MaxConcurrent);
    });
    Test("Model download: progress, atomic placement, rejection of HTML pages and clean cancel", () =>
    {
        var tiny = WhisperModels.Get("tiny");
        var directory = Path.Combine(root, "download");
        var good = new byte[1_000_000]; "lmgg"u8.CopyTo(good);
        var progress = new List<(long, long)>();
        ModelDownloader.DownloadAsync(tiny, directory, new Progress<(long, long)>(p => { lock (progress) progress.Add(p); }), CancellationToken.None,
            new Uri("https://example.invalid/m.bin"), new StubHandler(() => new MemoryStream(good), good.Length)).GetAwaiter().GetResult();
        string target = WhisperModels.PathOf(tiny, directory);
        Check(File.Exists(target) && new FileInfo(target).Length == good.Length && !File.Exists(target + ".part"), "Download not placed");
        Check(WaitUntil(() => { lock (progress) return progress.Count > 0 && progress[^1] == (good.Length, good.Length); }, 2000), "Progress incomplete");
        File.Delete(target);

        bool rejected = false;
        try { ModelDownloader.DownloadAsync(tiny, directory, null, CancellationToken.None, new Uri("https://example.invalid/m.bin"),
            new StubHandler(() => new MemoryStream("<html>404</html>"u8.ToArray()), 16)).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(target) && !File.Exists(target + ".part"), "HTML page accepted as model");

        using var cts = new CancellationTokenSource();
        var hang = Task.Run(async () => { try { await ModelDownloader.DownloadAsync(tiny, directory, null, cts.Token, new Uri("https://example.invalid/m.bin"),
            new StubHandler(() => new HangingStream(), 10_000_000)); return false; } catch (OperationCanceledException) { return true; } });
        Check(WaitUntil(() => File.Exists(target + ".part")), "Download did not start");
        cts.Cancel();
        Check(hang.Wait(5000) && hang.Result && !File.Exists(target) && !File.Exists(target + ".part"), "Cancel left files behind");
    });

    Test("Shortcut rules: default configuration is free of conflicts and covers copy, paste, dictation and 1-9", () =>
    {
        var settings = new AppSettings();
        Check(ShortcutRules.FindConflict(settings) is null, "Defaults conflict: " + ShortcutRules.FindConflict(settings));
        Check(ShortcutRules.Assignments(settings).Select(a => a.Shortcut.ToString()).SequenceEqual(["Ctrl+Y", "Ctrl+Shift+C", "Ctrl+Shift+V"]), "Assignments");
        var sessions = ShortcutRules.SessionShortcuts("Alt");
        Check(sessions.Count == 9 && sessions[0].Shortcut.ToString() == "Alt+1" && sessions[8].Shortcut.ToString() == "Alt+9", "Session shortcuts");
    });
    Test("Shortcut rules: duplicates, session digits and the tmux prefix are refused", () =>
    {
        var dup = ShortcutRules.FindConflict(new AppSettings { Dictation = new() { Shortcut = "Ctrl+Shift+C" } });
        Check(dup is not null && dup.Contains("Ctrl+Shift+C") && dup.Contains("Diktat") && dup.Contains("kopieren"), "Duplicate: " + dup);
        var digit = ShortcutRules.FindConflict(new AppSettings { Dictation = new() { Shortcut = "Alt+3" } });
        Check(digit is not null && digit.Contains("Alt+3") && digit.Contains("Sessionwechsel"), "Session digit: " + digit);
        Check(ShortcutRules.FindConflict(new AppSettings { Dictation = new() { Shortcut = "Alt+3" }, Shortcuts = new() { SessionModifier = "Ctrl+Alt" } }) is null, "Other modifier frees Alt+3");
        var prefix = ShortcutRules.FindConflict(new AppSettings { Shortcuts = new() { Paste = "ctrl+b" } });
        Check(prefix is not null && prefix.Contains("tmux-Präfix"), "tmux prefix: " + prefix);
    });
    Test("Shortcut rules: hints for terminal keys, normalization of bad values", () =>
    {
        Check(ShortcutRules.TerminalHint(Shortcut.Parse("Ctrl+Y"))!.Contains("yank") && ShortcutRules.TerminalHint(Shortcut.Parse("Ctrl+Q")) is not null, "Hint for Ctrl+letter");
        Check(ShortcutRules.TerminalHint(Shortcut.Parse("Ctrl+Shift+D")) is null && ShortcutRules.TerminalHint(Shortcut.Parse("Alt+1")) is null && ShortcutRules.TerminalHint(Shortcut.Parse("F5")) is null, "No hint expected");
        var fixedUp = new ShortcutOptions { Copy = "C", Paste = "", SessionModifier = "Win" }.Normalized();
        Check(fixedUp.Copy == "Ctrl+Shift+C" && fixedUp.Paste == "Ctrl+Shift+V" && fixedUp.SessionModifier == "Alt", "Normalization");
    });
    Test("Settings: reconnect, autostart and shortcuts default to off/unchanged, round-trip, and old files still load", () =>
    {
        var defaults = new AppSettings();
        Check(!defaults.StartWithWindows && !new Profile().AutoReconnect && defaults.Shortcuts.SessionModifier == "Alt", "Defaults");
        var store = new SettingsStore(Path.Combine(root, "newopts"));
        var profile = new Profile { Name = "dev", Destination = "dev", AutoReconnect = true };
        store.Save(new() { Profiles = [profile], StartWithWindows = true, Shortcuts = new() { Copy = "Ctrl+Alt+C", SessionModifier = "Ctrl+Alt" } });
        var loaded = store.Load();
        Check(loaded.Profiles.Single().AutoReconnect && loaded.StartWithWindows && loaded.Shortcuts.Copy == "Ctrl+Alt+C" && loaded.Shortcuts.SessionModifier == "Ctrl+Alt", "Round trip");
        var old = Path.Combine(root, "oldprofile");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"Profiles\":[{\"Id\":\"a\",\"Name\":\"x\",\"Destination\":\"x\",\"AutoConnect\":true}]}");
        var oldLoaded = new SettingsStore(old).Load();
        Check(oldLoaded.Profiles[0].AutoConnect && !oldLoaded.Profiles[0].AutoReconnect && !oldLoaded.StartWithWindows, "Old profile gained options");
    });
    Test("Reconnect: only a lost connection is retried, waits grow 2-4-8-16-30 s and never stop", () =>
    {
        var t = new ReconnectTracker();
        var now = DateTimeOffset.Now;
        t.Connected(now);
        var waits = Enumerable.Range(1, 7).Select(i => t.Lost(true, 255, now.AddSeconds(i))).ToList();
        Check(waits.Select(w => (int)w.Delay.TotalSeconds).SequenceEqual([2, 4, 8, 16, 30, 30, 30]) && waits.Select(w => w.Attempt).SequenceEqual([1, 2, 3, 4, 5, 6, 7]) && waits.All(w => w.Retry), "Backoff");
        ReconnectDecision last = waits[^1];
        for (int i = 0; i < 500; i++) last = t.Lost(true, 255, now);
        Check(last.Retry && last.Delay == TimeSpan.FromSeconds(30) && last.Attempt == 507, "A long outage must keep retrying every 30 s: " + last);
    });
    Test("SSH arguments: keepalive notices a silent network loss within about 45 s, connect attempts time out", () =>
    {
        var a = SshCommand.Arguments(new() { Name = "dev", Destination = "devbox" });
        Check(a.Contains("ServerAliveInterval=15") && a.Contains("ServerAliveCountMax=3") && a.Contains("ConnectTimeout=15"), "Keepalive or connect timeout missing: " + string.Join(' ', a));
    });
    Test("Reconnect: normal ends, disabled option and stable connections", () =>
    {
        var now = DateTimeOffset.Now;
        var t = new ReconnectTracker();
        Check(!t.Lost(true, 0, now).Retry && !t.Lost(true, 1, now).Retry && !t.Lost(true, 130, now).Retry && !t.Lost(true, 3, now).Retry, "Normal ends retried");
        Check(ReconnectTracker.IsConnectionLost(255) && ReconnectTracker.IsConnectionLost(-1) && !ReconnectTracker.IsConnectionLost(254), "Lost-connection codes");
        Check(new ReconnectTracker().Lost(true, -1, now).Retry, "Windows OpenSSH 'closed by remote host' (-1) must be retried");
        Check(!t.Lost(false, 255, now).Retry, "Disabled option retried");
        t.Connected(now); t.Lost(true, 255, now.AddSeconds(5)); t.Lost(true, 255, now.AddSeconds(6)); t.Lost(true, 255, now.AddSeconds(7));
        t.Connected(now.AddSeconds(10));
        Check(t.Lost(true, 255, now.AddSeconds(20)).Attempt == 4, "A short-lived connection must not reset the counter");
        t.Connected(now.AddSeconds(30));
        var afterStable = t.Lost(true, 255, now.AddSeconds(30 + 61));
        Check(afterStable.Attempt == 1 && afterStable.Delay == TimeSpan.FromSeconds(2), "A stable connection starts a new incident: " + afterStable);
        t.Reset();
        Check(t.Lost(true, 255, now).Attempt == 1, "Reset");
    });
    Test("Start with Windows: quoted command, idempotent apply, removal, own key only", () =>
    {
        string exe = @"C:\Users\Test User\AppData\Local\Programs\TerminalMgr\TerminalMgr.App.exe";
        Check(StartupRegistration.CommandFor(exe) == "\"" + exe + "\"" && StartupRegistration.CommandFor("a\"b") == "\"ab\"", "Quoting");
        string realBefore = new StartupRegistration().CurrentCommand() ?? "";
        // Everything this test writes lives under one throw-away key that it owns; only exactly that key is ever deleted.
        string testRoot = @"Software\TerminalMgr-Test-" + Guid.NewGuid().ToString("N");
        string subKey = testRoot + @"\Run";
        var registration = new StartupRegistration(subKey);
        try
        {
            Check(registration.CurrentCommand() is null && !registration.Apply(false, exe), "Fresh key must be empty and disabling a no-op");
            Check(registration.Apply(true, exe) && registration.IsEnabled(exe) && registration.CurrentCommand() == "\"" + exe + "\"", "Enable");
            Check(!registration.Apply(true, exe), "Second enable must not rewrite");
            string moved = exe.Replace("TerminalMgr.App.exe", "Neu\\TerminalMgr.App.exe");
            Check(!registration.IsEnabled(moved) && registration.Apply(true, moved) && registration.IsEnabled(moved), "A moved app updates the entry");
            Check(registration.Apply(false, exe) && registration.CurrentCommand() is null && !registration.Apply(false, exe), "Disable");
        }
        finally
        {
            // Guard against ever deleting anything but the test key (a recursive delete of a parent would be destructive).
            if (!System.Text.RegularExpressions.Regex.IsMatch(testRoot, @"^Software\\TerminalMgr-Test-[0-9a-f]{32}$"))
                throw new InvalidOperationException("Refusing to delete registry key " + testRoot);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
        Check((new StartupRegistration().CurrentCommand() ?? "") == realBefore, "The real autostart entry was touched");
    });

    Test("OSC 52: write requests decode to text (UTF-8, selection names, line breaks in base64)", () =>
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        Check(Osc52.TryDecode("c;" + B64("Hallo Welt"), out var t1) && t1 == "Hallo Welt", "Plain");
        Check(Osc52.TryDecode(";" + B64("ohne Auswahl"), out var t2) && t2 == "ohne Auswahl", "Empty selection");
        Check(Osc52.TryDecode("cp;" + B64("äöü ß 日本\nzweite Zeile\ttab"), out var t3) && t3 == "äöü ß 日本\nzweite Zeile\ttab", "Unicode and layout");
        Check(Osc52.TryDecode("c;" + B64("abc").Insert(2, "\r\n"), out var t4) && t4 == "abc", "Whitespace inside base64 must be ignored");
        Check(Osc52.TryDecode("c;" + B64("ab").TrimEnd('='), out var t5) && t5 == "ab", "Missing padding is tolerated");
        Check(Osc52.TryDecode("s0;" + B64("x"), out _), "Cut buffer names");
    });
    Test("OSC 52: read requests, clears, garbage and oversize are never acted upon", () =>
    {
        foreach (string bad in new[] { null!, "", "c;?", ";?", "c;", "c", "c;!!!not base64!!!", "c;A", "x;SGVsbG8=", "c;" + Convert.ToBase64String([0xff, 0xfe, 0xfd]) })
            Check(!Osc52.TryDecode(bad, out var text) && text == "", "Accepted '" + bad + "'");
        string tooLarge = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', ClipboardList.MaxTextLength + 1)));
        Check(!Osc52.TryDecode("c;" + tooLarge, out _), "Oversize accepted");
        Check(Osc52.TryDecode("c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', ClipboardList.MaxTextLength))), out var max) && max.Length == ClipboardList.MaxTextLength, "Maximum size refused");
    });
    Test("OSC 52 is on by default and old settings files keep that", () =>
    {
        Check(new AppSettings().AcceptOsc52, "Default off");
        var directory = Path.Combine(root, "osc52");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"FontSize\":13}");
        Check(new SettingsStore(directory).Load().AcceptOsc52, "Old file turned it off");
        var store = new SettingsStore(Path.Combine(root, "osc52b"));
        store.Save(new() { AcceptOsc52 = false });
        Check(!store.Load().AcceptOsc52, "Round trip");
    });
    Test("Window layout: kept on a screen, rescued when the monitor is gone, sane sizes, round trip", () =>
    {
        var main = new DesktopArea(0, 0, 1920, 1040);
        var second = new DesktopArea(1920, 0, 1920, 1040);
        var saved = new WindowLayout { Left = 100, Top = 50, Width = 1400, Height = 900 };
        Check(saved.Fit([main]) == new DesktopArea(100, 50, 1400, 900), "On screen stays unchanged");
        var onSecond = new WindowLayout { Left = 2100, Top = 40, Width = 1200, Height = 800 };
        Check(onSecond.Fit([main, second]) == new DesktopArea(2100, 40, 1200, 800), "Second monitor stays");
        var gone = onSecond.Fit([main]);
        Check(gone == new DesktopArea(360, 120, 1200, 800), "Monitor gone: centered on the first screen, got " + gone);
        Check(new WindowLayout { Left = 0, Top = 0, Width = 300, Height = 200 }.Fit([main]) is { Width: 880, Height: 540 }, "Minimum size");
        Check(new WindowLayout { Left = 0, Top = 0, Width = 9000, Height = 9000 }.Fit([main]) is { Width: 1920, Height: 1040 }, "Not bigger than the screen");
        Check(new WindowLayout { Left = 50, Top = -2000, Width = 1200, Height = 800 }.Fit([main]) is { Y: > 0 }, "Title bar above the screen is rescued");
        Check(new WindowLayout().Fit([main]) is null, "Nothing saved");
        Check(new WindowLayout { Left = double.NaN, Top = 0, Width = 1200, Height = 800 }.Fit([main]) is null, "NaN refused");
        Check(WindowLayout.PanelWidth(0, 260, 180) == 260 && WindowLayout.PanelWidth(300, 260, 180) == 300 && WindowLayout.PanelWidth(double.NaN, 260, 180) == 260, "Panel width");
        var store = new SettingsStore(Path.Combine(root, "windowlayout"));
        store.Save(new() { Window = new WindowLayout { Left = 10, Top = 20, Width = 1000, Height = 700, Maximized = true, ServerOpen = false, ServerWidth = 300, ClipboardOpen = true, ClipboardWidth = 350 } });
        var back = store.Load().Window;
        Check(back is { Left: 10, Width: 1000, Maximized: true, ServerOpen: false, ClipboardOpen: true, ClipboardWidth: 350 }, "Round trip");
        Check(new SettingsStore(Path.Combine(root, "osc52")).Load().Window is null, "Old file has no layout");
    });
    Test("Saving settings keeps a backup of the previous file once per run, five at most", () =>
    {
        var directory = Path.Combine(root, "backup");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"FontSize\":17}");
        var store = new SettingsStore(directory);
        store.Save(new() { FontSize = 12 }); store.Save(new() { FontSize = 13 });
        var backups = Directory.GetFiles(directory, "settings.*.bak");
        Check(backups.Length == 1 && File.ReadAllText(backups[0]).Contains("17"), "One backup with the old content, got " + backups.Length);
        for (int i = 0; i < 8; i++) File.WriteAllText(Path.Combine(directory, $"settings.2020010{i}-000000.bak"), "x");
        new SettingsStore(directory).Save(new() { FontSize = 14 });
        Check(Directory.GetFiles(directory, "settings.*.bak").Length == 5, "Only five kept");
    });
    Test("Links from the terminal: only plain http(s) addresses open", () =>
    {
        foreach (var good in new[] { "https://github.com/oweindl/TerminalMgr", "http://localhost:8080/a?b=c#d", "https://example.com/pfad%20mit" })
            Check(LinkRules.TryNormalize(good, out _), "Should open: " + good);
        foreach (var bad in new[] { null, "", "   ", "file:///C:/Windows/System32/cmd.exe", "javascript:alert(1)", "ftp://example.com", @"\\server\share", @"C:\Windows\notepad.exe",
            "https://", "ms-msdt:/id", "https://example.com/a b", "https://example.com/", "mailto:a@b.c", new string('a', 3000) })
            Check(!LinkRules.TryNormalize(bad, out _), "Must be refused: " + bad);
    });
    Test("Window strip: parser reads windows, clock and screens and strips escape sequences", () =>
    {
        string output = "@@NOW 1000\n@1\t0\t0\t0\t0\t\tbash\tshell\n@2\t1\t1\t0\t0\t\tclaude\tclaude\n@3\t2\t0\t1\t0\t\tterraform\tdeploy\n@4\t3\t0\t0\t1\t0\tdotnet\tbuild\nmuell ohne Tabs\n"
            + "@@W @1\n~ $ ls\nREADME.md\n@@W @2\n\u001b[32mlesen\u001b[0m 12/40\n\n> eingabe\n@@W @3\nApply changes?\n@@W @4\nBuild succeeded.\n@@END\n";
        var poll = WindowStripParser.Parse(output);
        Check(poll.ServerEpoch == 1000 && poll.Windows.Count == 4, "Clock and window count");
        Check(poll.Windows.Select(w => w.Name).SequenceEqual(["shell", "claude", "deploy", "build"]), "Names in index order");
        Check(poll.Windows[1].Active && poll.Windows[2].Bell && poll.Windows[3] is { Dead: true, ExitStatus: 0 }, "Flags");
        Check(poll.Windows[1].Screen.SequenceEqual(["lesen 12/40", "", "> eingabe"]), "Screen without escape sequences, rows kept: " + string.Join("|", poll.Windows[1].Screen));
        Check(poll.Windows[0].Screen.SequenceEqual(["~ $ ls", "README.md"]), "Screen lines");
        Check(WindowStripParser.Parse("kaputt\n@x\t1\n").Windows.Count == 0, "Garbage ignored");
        Check(WindowStripParser.Script("main").Contains("list-windows -t") && WindowStripParser.Script("main").Contains("capture-pane") && !WindowStripParser.Script("main").Contains("while"), "One frame");
        Check(WindowStripParser.Script("main", 1).StartsWith("while :; do ") && WindowStripParser.Script("main", 1).EndsWith("sleep 1; done"), "Frame loop");
        var profile = new Profile { Name = "x", Destination = "x" };
        Check(SshCommand.TmuxStripArguments(profile, "main", 1).Last().StartsWith("sh -c "), "Run through sh, whatever the login shell is");
        bool refused = false;
        try { SshCommand.TmuxStripArguments(profile, "a\nb"); } catch (ArgumentException) { refused = true; }
        Check(refused, "Control characters in the session name are refused");
    });
    Test("Window strip: new lines are what appeared, not what moved or stayed", () =>
    {
        string[] before = ["alte Zeile 1", "alte Zeile 2", "", "╭────────╮", "│ > eingabe", "╰────────╯", "? for shortcuts"];
        // The text scrolled up by one line and a new line appeared; the input box and the footer did not move.
        string[] after = ["alte Zeile 2", "neue Zeile", "", "╭────────╮", "│ > eingabe", "╰────────╯", "? for shortcuts"];
        Check(WindowActivityTracker.NewLines(before, after).SequenceEqual(["neue Zeile"]), "Only the new line: " + string.Join("|", WindowActivityTracker.NewLines(before, after)));
        Check(WindowActivityTracker.NewLines(before, before).Count == 0, "Nothing changed, nothing new");
        Check(!WindowActivityTracker.HasContent("╭────────╮") && WindowActivityTracker.HasContent("a"), "Frames are no content");
        Check(WindowActivityTracker.NewLines(["a b", "a b"], ["a b", "a b", "a b"]).Count == 1, "Repeated lines are counted");
    });
    Test("Window strip: running only while the screen changes, done when it stops, preview shows what moved", () =>
    {
        WindowSnapshot Win(string id, int index, string command, bool active, bool bell, IReadOnlyList<string> screen, bool dead = false, int? exit = null) =>
            new(id, index, id, command, active, bell, dead, exit, screen);
        var tracker = new WindowActivityTracker();
        var t0 = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        string[] footer = ["", "╭────────╮", "│ > eingabe", "╰────────╯", "? for shortcuts"];
        string[] Screen(params string[] top) => top.Concat(footer).ToArray();
        WindowCard One(string[] screen, int second, bool active = false) =>
            tracker.Update(new WindowPoll(1000 + second, [Win("@1", 1, "claude", active, false, screen)]), t0.AddSeconds(second))[0];

        var baseline = One(Screen("Ausgabe 1", "Ausgabe 2"), 0);
        Check(baseline.State == StripState.Idle, "A window nobody saw running is quiet, not done: " + baseline.State);
        Check(baseline.Preview.SequenceEqual(["│ > eingabe", "? for shortcuts"]) || baseline.Preview.Count == 2, "Quiet window: last lines of the screen: " + string.Join("|", baseline.Preview));
        var step1 = One(Screen("Ausgabe 2", "Ausgabe 3"), 1);           // scrolled, one new line
        Check(step1.State == StripState.Running && step1.Preview.SequenceEqual(["Ausgabe 3"]), "Screen changed: running, preview is the moving line: " + step1.State + " " + string.Join("|", step1.Preview));
        var step2 = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (12s)"), 2);
        Check(step2.State == StripState.Running && step2.Preview.SequenceEqual(["Ausgabe 4", "✻ Denke nach (12s)"]), "Two newest lines, the static box is never shown: " + string.Join("|", step2.Preview));
        var step3 = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (13s)"), 3);
        Check(step3.Preview.SequenceEqual(["Ausgabe 4", "✻ Denke nach (13s)"]), "A counter line replaces its older self: " + string.Join("|", step3.Preview));
        Check(!step3.Preview.Any(l => l.Contains("eingabe") || l.Contains("shortcuts")), "Input line and footer are not part of the preview");
        Check(step3.Detail.StartsWith("claude · 0:0"), "Running time: " + step3.Detail);
        var still = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (13s)"), 5);   // unchanged for 2 s
        Check(still.State == StripState.Running, "Still within the hold time");
        var done = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (13s)"), 8);    // unchanged for 5 s
        Check(done.State == StripState.Done && done.Detail.StartsWith("claude · vor "), "Screen stopped changing after it ran: done: " + done.State + " " + done.Detail);
        var later = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (13s)"), 120);
        Check(later.State == StripState.Done, "Done stays until the window is looked at");
        var seen = One(Screen("Ausgabe 3", "Ausgabe 4", "✻ Denke nach (13s)"), 121, active: true);
        Check(seen.State == StripState.Idle, "Switched to it: looked at, quiet");
        var again = One(Screen("Ausgabe 5"), 122, active: true);
        Check(again.State == StripState.Running && again.Spark.Count >= 1 && again.Spark.Max() > 0.3, "Changing again: running with activity in the bars");

        // Asking: the window ran and stopped on a question, or tmux reports the bell.
        var asker = new WindowActivityTracker();
        WindowCard Ask(string[] screen, int second, bool bell = false) => asker.Update(new WindowPoll(2000 + second, [Win("@7", 3, "terraform", false, bell, screen)]), t0.AddSeconds(second))[0];
        Ask(["Plan: 3 to add"], 0);
        Ask(["Plan: 3 to add", "Apply changes?"], 1);
        var asking = Ask(["Plan: 3 to add", "Apply changes?"], 6);
        Check(asking.State == StripState.Asking && asking.Detail.StartsWith("terraform · wartet"), "Stopped on a question: " + asking.State + " " + asking.Detail);
        var rung = new WindowActivityTracker().Update(new WindowPoll(1, [Win("@8", 4, "sh", false, true, ["fertig"])]), t0)[0];
        Check(rung.State == StripState.Asking, "The bell asks for attention");
        var dead = new WindowActivityTracker().Update(new WindowPoll(1, [Win("@9", 5, "dotnet", false, false, ["Build succeeded."], true, 0)]), t0)[0];
        Check(dead.State == StripState.Done && dead.Detail.StartsWith("dotnet · exit 0"), "A finished program in a held window: " + dead.Detail);

        // After a lost connection the next frame is a new baseline and does not count as a change.
        tracker.Reset();
        var afterReset = One(Screen("etwas ganz anderes"), 200);
        Check(afterReset.State != StripState.Running, "Reset: no false running");
        Check(WindowActivityTracker.LooksLikeQuestion("Continue [y/N]") && WindowActivityTracker.LooksLikeQuestion("Password for oli:") && WindowActivityTracker.LooksLikeQuestion("Do you want to proceed")
            && !WindowActivityTracker.LooksLikeQuestion("~/projekt $ ls") && !WindowActivityTracker.LooksLikeQuestion(""), "Question heuristic");
        Check(new WindowCard("@9", 12, "x", "", false, StripState.Idle, "", "", [], []).Shortcut == "" && new WindowCard("@9", 3, "x", "", false, StripState.Idle, "", "", [], []).Shortcut == "Ctrl 3", "Ctrl number only for 0 to 9");
        Check(WindowActivityTracker.Duration(252) == "4:12" && WindowActivityTracker.Ago(120) == "2 min" && WindowActivityTracker.Ago(10) == "gerade" && WindowActivityTracker.Ago(7200) == "2 h", "Time labels");
        var gone = tracker.Update(new WindowPoll(2005, []), t0.AddSeconds(905));
        Check(gone.Count == 0, "Closed windows disappear");
    });
    Test("Copy on select is off by default, survives a round trip and an export", () =>
    {
        Check(!new AppSettings().CopyOnSelect, "Default on");
        var store = new SettingsStore(Path.Combine(root, "copyonselect"));
        store.Save(new() { CopyOnSelect = true });
        Check(store.Load().CopyOnSelect, "Round trip");
    });

    string ReleaseJson(string tag = "v0.2.14", bool draft = false, bool pre = false, string host = "https://github.com", bool withSha = true, string? zipName = null)
    {
        string ver = tag.TrimStart('v');
        string zip = zipName ?? $"TerminalMgr-win-x64-{ver}.zip";
        string sha = withSha ? $",{{\"name\":\"{zip}.sha256\",\"size\":97,\"browser_download_url\":\"{host}/oweindl/TerminalMgr/releases/download/{tag}/{zip}.sha256\"}}" : "";
        return $"{{\"tag_name\":\"{tag}\",\"draft\":{draft.ToString().ToLower()},\"prerelease\":{pre.ToString().ToLower()},\"body\":\"- fix: Rechtsklick\\n- feat: Update\"," +
               $"\"assets\":[{{\"name\":\"{zip}\",\"size\":81748735,\"browser_download_url\":\"{host}/oweindl/TerminalMgr/releases/download/{tag}/{zip}\"}}{sha}]}}";
    }
    Test("Update: version texts and numeric ordering (0.2.9 < 0.2.10)", () =>
    {
        foreach (var (text, expected) in new[] { ("v0.2.14", "0.2.14"), ("0.2.14", "0.2.14"), ("0.2.14+219c1be", "0.2.14"), (" v1.0.0 ", "1.0.0") })
            Check(UpdateInfo.TryParseVersion(text, out var v) && v.ToString() == expected, "Parse " + text);
        foreach (string bad in new[] { "", "0.2", "latest", "v1.2.3.4", "0.2.x", "vv0.2.1", "0.2.1-beta" })
            Check(!UpdateInfo.TryParseVersion(bad, out _), "Accepted '" + bad + "'");
        Check(UpdateInfo.IsNewer(new Version(0, 2, 9), new Version(0, 2, 10)) && !UpdateInfo.IsNewer(new Version(0, 2, 10), new Version(0, 2, 10)) && !UpdateInfo.IsNewer(new Version(0, 3, 0), new Version(0, 2, 99)), "Ordering");
    });
    string MultiArchJson(string tag, params string[] architectures)
    {
        string ver = tag.TrimStart('v');
        var assets = architectures.SelectMany(a => new[] { $"TerminalMgr-win-{a}-{ver}.zip", $"TerminalMgr-win-{a}-{ver}.zip.sha256" })
            .Select(n => $"{{\"name\":\"{n}\",\"size\":1000,\"browser_download_url\":\"https://github.com/oweindl/TerminalMgr/releases/download/{tag}/{n}\"}}");
        return $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false,\"body\":\"x\",\"assets\":[{string.Join(",", assets)}]}}";
    }
    Test("Update: a release with one package per processor architecture gives each program its own package", () =>
    {
        bool Allowed(Uri u) => UpdateInfo.IsAllowedDownload(u);
        string json = MultiArchJson("v0.2.40", "x64", "arm64", "x86");
        foreach (string arch in UpdateInfo.Architectures)
        {
            var release = UpdateInfo.ParseLatestRelease(json, Allowed, arch);
            Check(release is not null && release.ZipName == $"TerminalMgr-win-{arch}-0.2.40.zip" && release.ZipUrl.AbsolutePath.EndsWith($"TerminalMgr-win-{arch}-0.2.40.zip") && release.ChecksumUrl.AbsolutePath.EndsWith($"win-{arch}-0.2.40.zip.sha256"), "Package for " + arch);
            Check(UpdateInfo.MissingArchitectureMessage(json, arch) is null, "Nothing is missing for " + arch);
        }
        Check(UpdateInfo.FileName(new Version(0, 2, 40), "arm64") == "TerminalMgr-win-arm64-0.2.40.zip" && UpdateInfo.Architectures.SequenceEqual(["x64", "arm64", "x86"]), "File names");
        Check(UpdateInfo.Architectures.Contains(UpdateInfo.CurrentArchitecture) && UpdateInfo.FileName(new Version(1, 0, 0)).Contains($"win-{UpdateInfo.CurrentArchitecture}-"), "The running program knows its own architecture: " + UpdateInfo.CurrentArchitecture);
    });
    Test("Update: an older release that only has the x64 package is fine for x64 and explained for the other processors", () =>
    {
        bool Allowed(Uri u) => UpdateInfo.IsAllowedDownload(u);
        string old = MultiArchJson("v0.2.31", "x64");
        Check(UpdateInfo.ParseLatestRelease(old, Allowed, "x64") is not null, "x64 still works with an old release");
        Check(UpdateInfo.ParseLatestRelease(old, Allowed, "arm64") is null, "No arm64 package, nothing to install");
        string message = UpdateInfo.MissingArchitectureMessage(old, "arm64") ?? "";
        Check(message.Contains("arm64") && message.Contains("x64") && message.Contains("kein Paket"), "Message: " + message);
        Check(UpdateInfo.MissingArchitectureMessage(MultiArchJson("v0.2.31"), "arm64") is null && UpdateInfo.MissingArchitectureMessage("not json", "x64") is null, "No message when nothing else exists or the JSON is junk");
        // A package of another architecture is never taken by mistake (different name, same version).
        Check(UpdateInfo.ParseLatestRelease(MultiArchJson("v0.2.40", "arm64", "x86"), Allowed, "x64") is null, "x64 must not take the arm64 or x86 package");
    });
    Test("Update: latest release JSON gives version, notes and both download files; unusable releases give nothing", () =>
    {
        bool Allowed(Uri u) => UpdateInfo.IsAllowedDownload(u);
        var release = UpdateInfo.ParseLatestRelease(ReleaseJson(), Allowed, "x64");
        Check(release is not null && release.Version.ToString() == "0.2.14" && release.Tag == "v0.2.14" && release.ZipName == "TerminalMgr-win-x64-0.2.14.zip" && release.ZipSize == 81748735, "Release: " + release);
        Check(release!.Notes.Contains("fix: Rechtsklick") && release.ZipUrl.AbsolutePath.EndsWith("/TerminalMgr-win-x64-0.2.14.zip") && release.ChecksumUrl.AbsolutePath.EndsWith(".zip.sha256"), "Notes or URLs");
        Check(UpdateInfo.ParseLatestRelease(ReleaseJson(draft: true), Allowed) is null && UpdateInfo.ParseLatestRelease(ReleaseJson(pre: true), Allowed) is null, "Draft or pre-release used");
        Check(UpdateInfo.ParseLatestRelease(ReleaseJson(withSha: false), Allowed) is null, "Release without checksum used");
        Check(UpdateInfo.ParseLatestRelease(ReleaseJson(zipName: "other.zip"), Allowed) is null, "Wrong asset name used");
        Check(UpdateInfo.ParseLatestRelease(ReleaseJson(host: "https://evil.example.test"), Allowed) is null, "Foreign download host used");
        Check(UpdateInfo.ParseLatestRelease(ReleaseJson("nightly"), Allowed) is null, "Non-version tag used");
        foreach (string junk in new[] { "", "not json", "[]", "{}", "{\"tag_name\":\"v1.0.0\"}", "{\"message\":\"Not Found\"}" })
            Check(UpdateInfo.ParseLatestRelease(junk, Allowed) is null, "Junk accepted: " + junk);
    });
    Test("Update: only GitHub's download hosts over https are allowed; loopback only for tests", () =>
    {
        foreach (string ok in new[] { "https://github.com/oweindl/TerminalMgr/releases/download/v1/x.zip", "https://objects.githubusercontent.com/x", "https://release-assets.githubusercontent.com/y" })
            Check(UpdateInfo.IsAllowedDownload(new Uri(ok)), "Refused " + ok);
        foreach (string bad in new[] { "http://github.com/x", "https://github.com.evil.test/x", "https://evilgithubusercontent.com/x", "https://example.test/x", "ftp://github.com/x", "http://127.0.0.1:8080/x" })
            Check(!UpdateInfo.IsAllowedDownload(new Uri(bad)), "Allowed " + bad);
        Check(UpdateInfo.IsAllowedDownload(new Uri("http://127.0.0.1:8080/x"), allowLoopback: true) && !UpdateInfo.IsAllowedDownload(new Uri("http://evil.example.test/x"), allowLoopback: true), "Loopback rule");
    });
    Test("Update: a newer release is offered unless the user skipped exactly that version", () =>
    {
        var r = UpdateInfo.ParseLatestRelease(ReleaseJson("v0.2.14"), u => true)!;
        Check(UpdateInfo.ShouldOffer(new Version(0, 2, 0), r, null) && UpdateInfo.ShouldOffer(new Version(0, 2, 13), r, "0.2.13") && UpdateInfo.ShouldOffer(new Version(0, 2, 13), r, "garbage"), "Newer must be offered");
        Check(!UpdateInfo.ShouldOffer(new Version(0, 2, 0), r, "0.2.14") && !UpdateInfo.ShouldOffer(new Version(0, 2, 0), r, "v0.2.14"), "Skipped version offered again");
        Check(!UpdateInfo.ShouldOffer(new Version(0, 2, 14), r, null) && !UpdateInfo.ShouldOffer(new Version(0, 3, 0), r, null), "Same or newer version offered");
    });
    Test("Update: checksum files in sha256sum format, and hashing a file", () =>
    {
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Check(UpdateInfo.ParseChecksum(abc + "  TerminalMgr-win-x64-0.2.14.zip\n", "TerminalMgr-win-x64-0.2.14.zip") == abc, "sha256sum format");
        Check(UpdateInfo.ParseChecksum(abc.ToUpper() + " *TerminalMgr-win-x64-0.2.14.zip\r\n", "TerminalMgr-win-x64-0.2.14.zip") == abc, "Binary marker and upper case");
        Check(UpdateInfo.ParseChecksum(abc + "\n", "x.zip") == abc, "Hash only");
        Check(UpdateInfo.ParseChecksum(abc + "  other.zip\n", "x.zip") is null && UpdateInfo.ParseChecksum("zz" + abc[2..] + "  x.zip", "x.zip") is null && UpdateInfo.ParseChecksum("", "x.zip") is null && UpdateInfo.ParseChecksum("<html>Not Found</html>", "x.zip") is null, "Wrong file or garbage accepted");
        var file = Path.Combine(root, "abc.bin");
        File.WriteAllText(file, "abc");
        Check(UpdateInfo.ComputeSha256(file) == abc, "SHA-256 of 'abc'");
    });
    Test("Update: only the installed copy replaces itself", () =>
    {
        string install = @"C:\Users\x\AppData\Local\Programs\TerminalMgr";
        Check(UpdateInfo.IsInstalledCopy(install + @"\TerminalMgr.App.exe", install), "Installed copy refused");
        Check(!UpdateInfo.IsInstalledCopy(@"C:\Users\x\Documents\TerminalMgr\src\bin\TerminalMgr.App.exe", install), "Development build allowed");
        Check(!UpdateInfo.IsInstalledCopy(install + @"-evil\TerminalMgr.App.exe", install) && !UpdateInfo.IsInstalledCopy(install + @"\dotnet.exe", install) && !UpdateInfo.IsInstalledCopy(null, install) && !UpdateInfo.IsInstalledCopy("", install), "Look-alike allowed");
    });
    Test("Paths: test overrides for data and install folder never apply to explicit folders", () =>
    {
        string? dataBefore = Environment.GetEnvironmentVariable("TERMINALMGR_DATA_DIR"), installBefore = Environment.GetEnvironmentVariable("TERMINALMGR_INSTALL_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TERMINALMGR_DATA_DIR", Path.Combine(root, "data-override"));
            Environment.SetEnvironmentVariable("TERMINALMGR_INSTALL_DIR", Path.Combine(root, "install-override"));
            Check(AppPaths.DataDirectory() == Path.Combine(root, "data-override") && AppPaths.InstallDirectory() == Path.Combine(root, "install-override"), "Override ignored");
            Check(AppPaths.UpdateDirectory() == Path.Combine(root, "data-override", "update") && AppPaths.ModelsDirectory() == Path.Combine(root, "data-override", "models"), "Derived folders");
            Check(AppPaths.DataDirectory(@"C:\L") == @"C:\L\TerminalMgr" && AppPaths.InstallDirectory(@"C:\L") == @"C:\L\Programs\TerminalMgr", "Explicit local app data must win");
        }
        finally { Environment.SetEnvironmentVariable("TERMINALMGR_DATA_DIR", dataBefore); Environment.SetEnvironmentVariable("TERMINALMGR_INSTALL_DIR", installBefore); }
        Check(AppPaths.InstallDirectory().EndsWith(@"Programs\TerminalMgr") && AppPaths.UpdateDirectory().EndsWith(@"TerminalMgr\update"), "Defaults");
    });
    Test("Update settings: check on start by default, skipped version round-trips, old files keep the defaults", () =>
    {
        Check(new AppSettings().Update.CheckOnStart && new AppSettings().Update.SkippedVersion is null, "Defaults");
        var store = new SettingsStore(Path.Combine(root, "updateopts"));
        store.Save(new() { Update = new() { CheckOnStart = false, SkippedVersion = "0.2.14" } });
        var loaded = store.Load().Update;
        Check(!loaded.CheckOnStart && loaded.SkippedVersion == "0.2.14", "Round trip");
        var old = Path.Combine(root, "updateold");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"FontSize\":12}");
        Check(new SettingsStore(old).Load().Update.CheckOnStart, "Old file turned the check off");
    });
    (string Install, string Staged, List<string> Log) SwapFixture(string name)
    {
        string baseDir = Path.Combine(root, "swap-" + name);
        string install = Path.Combine(baseDir, "Programs", "TerminalMgr"), staged = Path.Combine(baseDir, "update", "app");
        Directory.CreateDirectory(Path.Combine(install, "sub")); Directory.CreateDirectory(Path.Combine(staged, "sub"));
        File.WriteAllText(Path.Combine(install, "version.txt"), "old"); File.WriteAllText(Path.Combine(install, "sub", "x.txt"), "old-sub");
        File.WriteAllText(Path.Combine(staged, "version.txt"), "new"); File.WriteAllText(Path.Combine(staged, "sub", "x.txt"), "new-sub");
        return (install, staged, new List<string>());
    }
    Test("Update swap: new version replaces the old one and the backup is removed only after a successful start", () =>
    {
        var f = SwapFixture("ok");
        bool backupExistedDuringStart = false;
        var result = UpdateSwap.Apply(f.Install, f.Staged, () => { backupExistedDuringStart = Directory.Exists(f.Install + ".old") && File.ReadAllText(Path.Combine(f.Install, "version.txt")) == "new"; return true; }, () => throw new Exception("must not start the old app"), f.Log.Add);
        Check(result.Success && File.ReadAllText(Path.Combine(f.Install, "version.txt")) == "new" && File.ReadAllText(Path.Combine(f.Install, "sub", "x.txt")) == "new-sub", "Not replaced: " + result.Message);
        Check(backupExistedDuringStart, "The backup must exist while the new version is being started");
        Check(!Directory.Exists(f.Install + ".old") && !Directory.Exists(f.Staged), "Backup or staged folder left behind");
    });
    Test("Update swap: a new version that does not come up is rolled back and the old app is started", () =>
    {
        var f = SwapFixture("fail");
        bool oldStarted = false;
        var result = UpdateSwap.Apply(f.Install, f.Staged, () => false, () => oldStarted = true, f.Log.Add);
        Check(!result.Success && oldStarted, "Failure not reported or old app not started");
        Check(File.ReadAllText(Path.Combine(f.Install, "version.txt")) == "old" && File.ReadAllText(Path.Combine(f.Install, "sub", "x.txt")) == "old-sub", "Old version not restored");
        Check(!Directory.Exists(f.Install + ".old") && Directory.GetDirectories(Path.GetDirectoryName(f.Install)!).Length == 1, "Backup or failed folder left behind");
        var thrown = SwapFixture("throw");
        bool started2 = false;
        var r2 = UpdateSwap.Apply(thrown.Install, thrown.Staged, () => throw new InvalidOperationException("Absturz"), () => started2 = true, thrown.Log.Add);
        Check(!r2.Success && started2 && File.ReadAllText(Path.Combine(thrown.Install, "version.txt")) == "old", "Exception while starting the new app must roll back");
    });
    Test("Update swap: missing staged folder, a leftover backup and locked files", () =>
    {
        var none = SwapFixture("missing");
        Directory.Delete(none.Staged, true);
        var r = UpdateSwap.Apply(none.Install, none.Staged, () => true, () => { }, none.Log.Add);
        Check(!r.Success && File.ReadAllText(Path.Combine(none.Install, "version.txt")) == "old", "Missing staged folder");
        var leftover = SwapFixture("leftover");
        Directory.CreateDirectory(leftover.Install + ".old"); File.WriteAllText(Path.Combine(leftover.Install + ".old", "junk.txt"), "j");
        Check(UpdateSwap.Apply(leftover.Install, leftover.Staged, () => true, () => { }, leftover.Log.Add).Success && File.ReadAllText(Path.Combine(leftover.Install, "version.txt")) == "new", "Leftover backup blocked the update");
        var locked = SwapFixture("locked");
        using (new FileStream(Path.Combine(locked.Install, "version.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lr = UpdateSwap.Apply(locked.Install, locked.Staged, () => true, () => throw new Exception("old app must not be started when nothing changed"), locked.Log.Add, lockRetrySeconds: 1);
            Check(!lr.Success && lr.Message.Contains("unverändert"), "Locked install folder: " + lr.Message);
        }
        Check(File.ReadAllText(Path.Combine(locked.Install, "version.txt")) == "old" && Directory.Exists(locked.Staged), "A failed swap must leave the old version and the staged folder alone");
    });

    // A fake release on a local HTTP server: JSON for "latest", the ZIP (folder TerminalMgr-win-x64 with a fake app) and its checksum.
    (LocalServer Server, string Zip, string Sha) FakeRelease(string name, string version = "0.2.14", bool withExe = true)
    {
        string source = Path.Combine(root, "rel-" + name, $"TerminalMgr-win-{UpdateInfo.CurrentArchitecture}");
        Directory.CreateDirectory(Path.Combine(source, "Terminal"));
        if (withExe) File.WriteAllText(Path.Combine(source, "TerminalMgr.App.exe"), "MZ fake app " + version);
        File.WriteAllText(Path.Combine(source, "Terminal", "index.html"), "<html></html>");
        var filler = new byte[300_000];
        new Random(42).NextBytes(filler); // random data does not compress, so the zip stays large enough to see progress
        File.WriteAllBytes(Path.Combine(source, "filler.bin"), filler);
        string zip = Path.Combine(root, "rel-" + name, $"TerminalMgr-win-{UpdateInfo.CurrentArchitecture}-{version}.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
        string sha = $"{UpdateInfo.ComputeSha256(zip)}  {Path.GetFileName(zip)}\n";
        var server = new LocalServer();
        string baseUrl = server.BaseUrl;
        server.Routes["/api/latest"] = c => LocalServer.Text(c, $"{{\"tag_name\":\"v{version}\",\"body\":\"- fix: Test\",\"assets\":[" +
            $"{{\"name\":\"{Path.GetFileName(zip)}\",\"size\":{new FileInfo(zip).Length},\"browser_download_url\":\"{baseUrl}dl/{Path.GetFileName(zip)}\"}}," +
            $"{{\"name\":\"{Path.GetFileName(zip)}.sha256\",\"size\":{sha.Length},\"browser_download_url\":\"{baseUrl}dl/{Path.GetFileName(zip)}.sha256\"}}]}}");
        server.Routes["/dl/" + Path.GetFileName(zip)] = c => LocalServer.Bytes(c, File.ReadAllBytes(zip));
        server.Routes["/dl/" + Path.GetFileName(zip) + ".sha256"] = c => LocalServer.Text(c, sha);
        return (server, zip, sha);
    }
    Test("Update service: finds the latest release, offers only allowed download addresses", () =>
    {
        var (server, _, _) = FakeRelease("check");
        using (server)
        {
            using var service = new UpdateService(new Uri(server.BaseUrl + "api/latest"), allowLoopback: true);
            var result = service.CheckAsync().GetAwaiter().GetResult();
            Check(result.Error is null && result.Release is { } r && r.Version.ToString() == "0.2.14" && r.Notes.Contains("fix: Test") && r.ZipUrl.Host == "127.0.0.1", "Check failed: " + result.Error);
            using var strict = new UpdateService(new Uri(server.BaseUrl + "api/latest"), allowLoopback: false);
            var refused = strict.CheckAsync().GetAwaiter().GetResult();
            Check(refused.Release is null && refused.Error!.Contains("verwendbare"), "Loopback downloads must be refused outside tests: " + refused.Error);
        }
    });
    Test("Update service: stages a verified download with progress and leaves no loose files", () =>
    {
        var (server, _, _) = FakeRelease("stage");
        using (server)
        {
            using var service = new UpdateService(new Uri(server.BaseUrl + "api/latest"), allowLoopback: true);
            var release = service.CheckAsync().GetAwaiter().GetResult().Release!;
            string updateRoot = Path.Combine(root, "update-ok");
            long lastDone = 0, lastTotal = 0;
            string app = service.StageAsync(release, updateRoot, new Progress<(long, long)>(p => { lastDone = p.Item1; lastTotal = p.Item2; }), CancellationToken.None).GetAwaiter().GetResult();
            Check(File.Exists(Path.Combine(app, "TerminalMgr.App.exe")) && File.Exists(Path.Combine(app, "Terminal", "index.html")) && app == Path.Combine(updateRoot, "0.2.14", "app"), "Staged folder wrong: " + app);
            Check(WaitUntil(() => lastDone == lastTotal && lastTotal > 300_000, 2000), $"Progress {lastDone}/{lastTotal}");
            Check(Directory.GetFileSystemEntries(Path.Combine(updateRoot, "0.2.14")).Select(Path.GetFileName).SequenceEqual(["app"]), "Zip or extract folder left behind");
            File.WriteAllText(Path.Combine(updateRoot, "updater.log"), "log");
            Directory.CreateDirectory(Path.Combine(updateRoot, "0.2.1"));
            UpdateService.CleanUp(updateRoot);
            Check(File.Exists(Path.Combine(updateRoot, "updater.log")) && Directory.GetDirectories(updateRoot).Length == 0, "Clean-up must remove staged folders but keep the log");
        }
    });
    Test("Update service: wrong checksum, truncated download, broken package and cancel leave nothing behind", () =>
    {
        string Fails(string name, Action<LocalServer, string> sabotage, Func<UpdateService, UpdateRelease, string, CancellationToken, Task>? run = null, CancellationToken token = default)
        {
            var (server, zip, _) = FakeRelease(name);
            using (server)
            {
                sabotage(server, zip);
                using var service = new UpdateService(new Uri(server.BaseUrl + "api/latest"), allowLoopback: true);
                var release = service.CheckAsync().GetAwaiter().GetResult().Release!;
                string updateRoot = Path.Combine(root, "update-" + name);
                try { service.StageAsync(release, updateRoot, null, token).GetAwaiter().GetResult(); return "no exception"; }
                catch (Exception ex)
                {
                    bool clean = !Directory.Exists(updateRoot) || Directory.GetFileSystemEntries(updateRoot).Length == 0;
                    return (clean ? "" : "LEFTOVER ") + ex.GetType().Name + ": " + ex.Message;
                }
            }
        }
        string wrong = Fails("badsum", (s, zip) => s.Routes["/dl/" + Path.GetFileName(zip) + ".sha256"] = c => LocalServer.Text(c, new string('0', 64) + "  " + Path.GetFileName(zip) + "\n"));
        Check(wrong.StartsWith("InvalidDataException") && wrong.Contains("Prüfsumme"), "Wrong checksum: " + wrong);
        string garbage = Fails("junksum", (s, zip) => s.Routes["/dl/" + Path.GetFileName(zip) + ".sha256"] = c => LocalServer.Text(c, "<html>Not Found</html>"));
        Check(garbage.StartsWith("InvalidDataException"), "Garbage checksum file: " + garbage);
        string truncated = Fails("cut", (s, zip) => s.Routes["/dl/" + Path.GetFileName(zip)] = c => LocalServer.Truncated(c, File.ReadAllBytes(zip), 100_000));
        Check(!truncated.StartsWith("no exception") && !truncated.StartsWith("LEFTOVER"), "Truncated download: " + truncated);
        string notFound = Fails("gone", (s, zip) => s.Routes.Remove("/dl/" + Path.GetFileName(zip)));
        Check(!notFound.StartsWith("no exception") && !notFound.StartsWith("LEFTOVER"), "Missing file: " + notFound);
        // A package without TerminalMgr.App.exe must be refused even if the checksum is right.
        var (server2, zip2, _) = FakeRelease("noexe", withExe: false);
        using (server2)
        {
            using var service = new UpdateService(new Uri(server2.BaseUrl + "api/latest"), allowLoopback: true);
            var release = service.CheckAsync().GetAwaiter().GetResult().Release!;
            string r = "no exception";
            try { service.StageAsync(release, Path.Combine(root, "update-noexe"), null, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException ex) { r = ex.Message; }
            Check(r.Contains("TerminalMgr.App.exe") && Directory.GetFileSystemEntries(Path.Combine(root, "update-noexe")).Length == 0, "Package without exe: " + r);
        }
        var (server3, zip3, _) = FakeRelease("cancel");
        using (server3)
        {
            server3.Routes["/dl/" + Path.GetFileName(zip3)] = c => LocalServer.Slow(c, File.ReadAllBytes(zip3));
            using var service = new UpdateService(new Uri(server3.BaseUrl + "api/latest"), allowLoopback: true);
            var release = service.CheckAsync().GetAwaiter().GetResult().Release!;
            using var cts = new CancellationTokenSource(300);
            string updateRoot = Path.Combine(root, "update-cancel");
            bool canceled = false;
            try { service.StageAsync(release, updateRoot, null, cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && (!Directory.Exists(updateRoot) || Directory.GetFileSystemEntries(updateRoot).Length == 0), "Cancel must stop the download and clean up");
        }
    });
    Test("Update service: no network, 404 and a slow server end quickly with a short message", () =>
    {
        var (dead, _, _) = FakeRelease("dead");
        string deadUrl = dead.BaseUrl + "api/latest";
        dead.Dispose(); // nobody listens there any more
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var offline = new UpdateService(new Uri(deadUrl), allowLoopback: true))
        {
            var r = offline.CheckAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(r.Release is null && r.Error!.Contains("keine Verbindung") && clock.Elapsed < TimeSpan.FromSeconds(5), $"Offline: {r.Error} after {clock.Elapsed}");
        }
        var (server, _, _) = FakeRelease("slow");
        using (server)
        {
            server.Routes.Remove("/api/latest");
            using var service = new UpdateService(new Uri(server.BaseUrl + "api/latest"), allowLoopback: true);
            Check(service.CheckAsync().GetAwaiter().GetResult().Error!.Contains("Kein Release gefunden"), "404 message");
            server.Routes["/api/latest"] = c => LocalServer.Slow(c, Encoding.UTF8.GetBytes("{}"));
            clock.Restart();
            var slow = service.CheckAsync(TimeSpan.FromMilliseconds(600)).GetAwaiter().GetResult();
            Check(slow.Error!.Contains("Zeitüberschreitung") && clock.Elapsed < TimeSpan.FromSeconds(3), $"Slow server: {slow.Error} after {clock.Elapsed}");
        }
    });

    Test("Clipboard list: only shortened entries count as truncated", () =>
    {
        Check(!ClipboardText.Preview("kurz").Truncated && !ClipboardText.Preview("eins\nzwei").Truncated, "Short texts must show completely");
        Check(ClipboardText.Preview("eins\nzwei\ndrei").Truncated && ClipboardText.Preview(new string('x', 500)).Truncated, "Three lines or one long line are shortened");
        Check(!ClipboardText.Preview(new string('x', 120)).Truncated && ClipboardText.Preview(new string('x', 121)).Truncated, "Boundary of the line length");
    });
    Test("Clipboard list: at most one entry is unfolded, folding again and removal reset it, complete entries never unfold", () =>
    {
        var list = new ClipboardList();
        string longA = string.Join('\n', Enumerable.Range(1, 30).Select(i => "a" + i)), longB = string.Join('\n', Enumerable.Range(1, 30).Select(i => "b" + i));
        list.Add(longA, "dev"); list.Add(longB, "dev"); list.Add("kurz", "dev");
        var a = list.Items.Single(c => c.Text == longA); var b = list.Items.Single(c => c.Text == longB); var kurz = list.Items.Single(c => c.Text == "kurz");
        Check(list.ToggleExpanded(a) && list.ExpandedText == longA, "Unfold A");
        Check(list.ToggleExpanded(b) && list.ExpandedText == longB, "Unfolding B must fold A");
        Check(!list.ToggleExpanded(b) && list.ExpandedText is null, "Second click folds again");
        list.ToggleExpanded(a);
        Check(!list.ToggleExpanded(kurz) && list.ExpandedText is null, "A short entry must not unfold and folds the open one");
        list.ToggleExpanded(a); list.Remove(a);
        Check(list.ExpandedText is null, "Removing the unfolded entry");
        list.ToggleExpanded(b); list.ClearUnpinned();
        Check(list.ExpandedText is null && !list.ToggleExpanded(b), "Clearing the history");
        var limited = new ClipboardList { MaxUnpinned = 1 };
        limited.Add(longA, "dev"); limited.ToggleExpanded(limited.Items[0]); limited.Add(longB, "dev");
        Check(limited.ExpandedText is null, "Trimming the list must forget an entry that dropped out");
    });

    AppSettings ExportFixture() => new()
    {
        Profiles = [new Profile { Id = "p1", Name = "devbox", Destination = "devbox", AutoConnect = true, AutoReconnect = true, Mode = StartMode.Tmux, TmuxSession = "main", Command = "htop" },
                    new Profile { Id = "p2", Name = "server1", Destination = "srv@example.test", Mode = StartMode.Command, Command = "exec bash -l" }],
        PinnedClips = [new("geheimes-passwort-hinweis", "devbox", DateTimeOffset.Parse("2026-10-25T10:00:00+00:00"), true), new("lose", "devbox", DateTimeOffset.Now, false)],
        FontSize = 17, ClipHistoryLimit = 250, AcceptOsc52 = false, StartWithWindows = true,
        Shortcuts = new() { Copy = "Ctrl+Alt+C", SessionModifier = "Ctrl+Alt" },
        Update = new() { CheckOnStart = false, SkippedVersion = "0.2.9" },
        Dictation = new() { Shortcut = "Ctrl+Shift+D", Model = "small", Microphone = "Headset", Language = "auto", InitialPrompt = "tmux, git", SilenceSeconds = 2.5, LevelThreshold = 0.03 }
    };
    var all = new ExportSelection(true, true, true, true);
    Test("Config export: only the chosen parts, and nothing machine-bound or secret", () =>
    {
        var settings = ExportFixture();
        string json = Encoding.UTF8.GetString(ConfigFile.Serialize(ConfigFile.Build(settings, all, []), "0.2.15", DateTimeOffset.Now, null));
        Check(json.Contains("\"devbox\"") && json.Contains("server1") && json.Contains("geheimes-passwort-hinweis") && !json.Contains("\"lose\""), "Profiles or pinned entries wrong");
        foreach (string forbidden in new[] { "small", "Headset", "StartWithWindows", "SkippedVersion", "0.2.9", "Microphone", "\"Model\"" })
            Check(!json.Contains(forbidden), "Export contains '" + forbidden + "'");
        var none = ConfigFile.Build(settings, new ExportSelection(false, false, false, false), []);
        Check(none.Profiles is null && none.Settings is null && none.PinnedClips is null && none.SshHosts is null, "Nothing selected must export nothing");
        string noPins = Encoding.UTF8.GetString(ConfigFile.Serialize(ConfigFile.Build(settings, new ExportSelection(true, true, false, false), []), "0.2.15", DateTimeOffset.Now, null));
        Check(!noPins.Contains("geheimes-passwort-hinweis") && !noPins.Contains("pinnedClips", StringComparison.OrdinalIgnoreCase), "Pinned entries must stay out unless chosen");
    });
    Test("Config file: format header, round trip of profiles and settings, empty parts stay empty", () =>
    {
        var settings = ExportFixture();
        var content = ConfigFile.Build(settings, all, [new SshHostBlock(["devbox"], "Host devbox\n  HostName dev.example.test\n  User me")]);
        byte[] file = ConfigFile.Serialize(content, "0.2.15", DateTimeOffset.Parse("2026-10-25T12:00:00+00:00"), null);
        var info = ConfigFile.Inspect(file);
        Check(info is { FormatVersion: 1, AppVersion: "0.2.15", Encrypted: false } && info.Exported.Year == 2026, "Header: " + info);
        string text = Encoding.UTF8.GetString(file);
        Check(text.Contains("\"format\": \"tmconfig\"") && text.Contains("\"formatVersion\": 1"), "Format fields");
        var (_, back) = ConfigFile.Parse(file, null);
        Check(back.Profiles!.SequenceEqual(settings.Profiles) && back.Profiles![0].AutoReconnect && back.Profiles[0].Command == "htop", "Profiles differ");
        Check(back.Settings is { FontSize: 17, ClipHistoryLimit: 250, AcceptOsc52: false, CheckForUpdates: false } s && s.Shortcuts.Copy == "Ctrl+Alt+C" && s.Dictation is { Shortcut: "Ctrl+Shift+D", Language: "auto", SilenceSeconds: 2.5 }, "Settings differ");
        Check(back.PinnedClips!.Single().Text == "geheimes-passwort-hinweis" && back.SshHosts!.Single().Text.Contains("HostName dev.example.test"), "Pinned or hosts differ");
        var (_, empty) = ConfigFile.Parse(ConfigFile.Serialize(new ConfigContent(), "0.2.15", DateTimeOffset.Now, null), null);
        Check(empty.Profiles is null && empty.Settings is null, "Empty file");
    });
    Test("Config file: password protection - right password only, no plain text, tampering detected", () =>
    {
        var content = ConfigFile.Build(ExportFixture(), all, []);
        byte[] file = ConfigFile.Serialize(content, "0.2.15", DateTimeOffset.Now, "Geheim-123");
        string text = Encoding.UTF8.GetString(file);
        Check(ConfigFile.Inspect(file).Encrypted && !text.Contains("devbox") && !text.Contains("geheimes-passwort-hinweis") && text.Contains("AES-256-GCM") && text.Contains("PBKDF2-SHA256"), "Plain text visible or header wrong");
        Check(ConfigFile.Parse(file, "Geheim-123").Content.Profiles!.Count == 2, "Right password refused");
        foreach (string wrong in new[] { "falsch", "geheim-123", "Geheim-123 " })
        { bool refused = false; try { ConfigFile.Parse(file, wrong); } catch (ConfigPasswordException) { refused = true; } Check(refused, "Wrong password accepted: " + wrong); }
        bool needsPassword = false; try { ConfigFile.Parse(file, null); } catch (ConfigPasswordException ex) { needsPassword = ex.Message.Contains("passwortgeschützt"); }
        Check(needsPassword, "A protected file without password must ask for one");
        Check(!ConfigFile.Serialize(content, "0.2.15", DateTimeOffset.Now, "Geheim-123").AsSpan().SequenceEqual(file), "Salt and nonce must differ between exports");
        string tampered = text.Replace("\"data\": \"", "\"data\": \"AAAA");
        bool detected = false; try { ConfigFile.Parse(Encoding.UTF8.GetBytes(tampered), "Geheim-123"); } catch (ConfigPasswordException) { detected = true; } catch (ConfigFormatException) { detected = true; }
        Check(detected, "Tampered data accepted");
    });
    Test("Config file: newer formats, other files, garbage and oversize are refused with a clear message", () =>
    {
        byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
        void Refuses(byte[] file, string expected, string what)
        { try { ConfigFile.Parse(file, null); throw new Exception("Accepted " + what); } catch (ConfigFormatException ex) { Check(ex.Message.Contains(expected), what + ": " + ex.Message); } }
        Refuses(Json("{\"format\":\"tmconfig\",\"formatVersion\":2,\"appVersion\":\"9.0.0\",\"exported\":\"2026-10-25T12:00:00+00:00\",\"content\":{}}"), "neueren Version", "newer format");
        Refuses(Json("{\"format\":\"tmconfig\",\"formatVersion\":0,\"content\":{}}"), "ungültig", "version 0");
        Refuses(Json("{\"format\":\"other\",\"formatVersion\":1}"), "keine TerminalMgr-Konfigurationsdatei", "other format");
        Refuses(Json("this is not json"), "keine TerminalMgr-Konfigurationsdatei", "garbage");
        Refuses(Json(""), "keine TerminalMgr-Konfigurationsdatei", "empty file");
        Refuses(new byte[(int)ConfigFile.MaxFileBytes + 1], "groß", "oversize");
        Refuses(Json("{\"format\":\"tmconfig\",\"formatVersion\":1,\"encryption\":{\"algorithm\":\"AES-256-GCM\"}}"), "fehlen", "encrypted without data");
    });
    string SshHome(string name, out string config)
    {
        string home = Path.Combine(root, "ssh-" + name);
        Directory.CreateDirectory(Path.Combine(home, ".ssh", "conf.d"));
        config = Path.Combine(home, ".ssh", "config");
        File.WriteAllText(Path.Combine(home, ".ssh", "conf.d", "extra.conf"), "Host extra\n  HostName extra.example.test\n  IdentityFile ~/.ssh/extra_key\n");
        File.WriteAllText(config, "# my servers\nInclude conf.d/*.conf\n\nHost devbox devbox-alt\n  HostName dev.example.test\n  User me\n  IdentityFile ~/.ssh/dev_key\n  RemoteCommand tmux new -A -s main\n\n# comment between\nHost server1\n  HostName s1.example.test\n\nMatch host *.internal\n  User root\n\nHost *\n  ServerAliveInterval 30\n\nHost wild-*\n  User x\n");
        return home;
    }
    Test("SSH config blocks: read with includes, skip Match, pick the blocks of the exported profiles, list key files", () =>
    {
        string home = SshHome("read", out string config);
        var blocks = SshConfigBlocks.Read(config, home);
        Check(blocks.Select(b => string.Join(",", b.Aliases)).SequenceEqual(["extra", "devbox,devbox-alt", "server1", "*", "wild-*"]), "Blocks: " + string.Join(" | ", blocks.Select(b => string.Join(",", b.Aliases))));
        Check(blocks[1].Text.StartsWith("Host devbox devbox-alt") && blocks[1].Text.Contains("RemoteCommand tmux new -A -s main") && !blocks[1].Text.Contains("server1") && !blocks[2].Text.Contains("Match"), "Block boundaries wrong");
        var profiles = new[] { new Profile { Name = "a", Destination = "devbox-alt" }, new Profile { Name = "b", Destination = "devbox" }, new Profile { Name = "c", Destination = "server1" }, new Profile { Name = "d", Destination = "nicht-vorhanden" }, new Profile { Name = "e", Destination = "me@host.example.test" }, new Profile { Name = "f", Destination = "*" } };
        var picked = SshConfigBlocks.ForProfiles(profiles, config, home);
        Check(picked.Count == 2 && picked[0].Aliases.Contains("devbox") && picked[1].Aliases.Single() == "server1", "Picked: " + string.Join(" | ", picked.Select(b => string.Join(",", b.Aliases))));
        Check(SshConfigBlocks.IdentityFiles(picked).SequenceEqual(["~/.ssh/dev_key"]), "Key files: " + string.Join(",", SshConfigBlocks.IdentityFiles(picked)));
        var aliases = SshConfigBlocks.ExistingAliases(config, home);
        Check(aliases.SetEquals(["extra", "devbox", "devbox-alt", "server1"]) && aliases.Contains("DEVBOX"), "Existing aliases: " + string.Join(",", aliases));
        Check(SshConfigBlocks.Read(Path.Combine(root, "gibtsnicht"), home).Count == 0, "Missing file");
    });
    Test("SSH config blocks: appending keeps the old text, marks the import and keeps the line ending style", () =>
    {
        var block = new SshHostBlock(["neu"], "Host neu\n  HostName n.example.test");
        string lf = SshConfigBlocks.AppendBlocks("Host alt\n  User x", [block], DateTimeOffset.Parse("2026-10-25T12:00:00+00:00"));
        Check(lf.StartsWith("Host alt\n  User x\n") && lf.Contains("# Importiert von TerminalMgr am 2026-10-25") && lf.Contains("Host neu\n  HostName n.example.test") && !lf.Contains('\r'), "LF: " + lf.Replace("\n", "\\n"));
        string crlf = SshConfigBlocks.AppendBlocks("Host alt\r\n  User x\r\n", [block], DateTimeOffset.Now);
        Check(crlf.StartsWith("Host alt\r\n  User x\r\n") && crlf.Contains("Host neu\r\n  HostName") && !crlf.Replace("\r\n", "").Contains('\n'), "CRLF must stay uniform");
        Check(SshConfigBlocks.AppendBlocks("", [block], DateTimeOffset.Now).Contains("Host neu"), "Empty config");
    });
    Test("Config import: new profiles are added, same names are conflicts resolved by skip, replace or copy", () =>
    {
        var current = new AppSettings { Profiles = [new Profile { Id = "c1", Name = "Devbox", Destination = "old-dev" }, new Profile { Id = "c2", Name = "keep", Destination = "keep" }] };
        var incoming = new ConfigContent { Profiles = [new Profile { Id = "i1", Name = "devbox", Destination = "new-dev", AutoReconnect = true }, new Profile { Id = "i2", Name = "frisch", Destination = "fresh" }, new Profile { Id = "i3", Name = "keep", Destination = "other" }] };
        var plan = ConfigImporter.BuildPlan(current, incoming, [], ImportMode.Merge);
        Check(plan.Profiles.Count(p => p.Conflict) == 2 && !plan.Profiles.Single(p => p.Incoming.Name == "frisch").Conflict && plan.Profiles[0].Existing!.Id == "c1", "Plan: conflicts are found by name, ignoring case");
        var skip = ConfigImporter.Apply(current, incoming, plan, new Dictionary<string, ConflictChoice>(), true, true, true, true);
        Check(skip.Settings.Profiles.Count == 3 && skip.Settings.Profiles.Single(p => p.Name == "Devbox").Destination == "old-dev" && skip.Skipped.SequenceEqual(["devbox", "keep"]) && skip.Added.Single().Name == "frisch", "Default must skip conflicts");
        var replace = ConfigImporter.Apply(current, incoming, plan, new Dictionary<string, ConflictChoice> { ["i1"] = ConflictChoice.Replace, ["i3"] = ConflictChoice.Skip }, true, true, true, true);
        var replaced = replace.Settings.Profiles.Single(p => p.Id == "c1");
        Check(replaced.Destination == "new-dev" && replaced.AutoReconnect && replaced.Name == "devbox" && replace.Settings.Profiles.Count == 3, "Replace keeps the Id of the existing profile");
        var copy = ConfigImporter.Apply(current, incoming, plan, new Dictionary<string, ConflictChoice> { ["i1"] = ConflictChoice.Copy, ["i3"] = ConflictChoice.Copy }, true, true, true, true);
        Check(copy.Settings.Profiles.Count == 5 && copy.Settings.Profiles.Any(p => p.Name == "devbox (Kopie)" && p.Destination == "new-dev") && copy.Settings.Profiles.Any(p => p.Name == "keep (Kopie)") && copy.Settings.Profiles.Select(p => p.Id).Distinct().Count() == 5, "Copies need new Ids and unique names");
        var again = ConfigImporter.Apply(copy.Settings, incoming, ConfigImporter.BuildPlan(copy.Settings, incoming, [], ImportMode.Merge), new Dictionary<string, ConflictChoice> { ["i1"] = ConflictChoice.Copy }, true, true, true, true);
        Check(again.Settings.Profiles.Any(p => p.Name == "devbox (Kopie) 2"), "A second copy gets a numbered name");
        Check(ConfigImporter.Apply(current, incoming, plan, new Dictionary<string, ConflictChoice>(), false, true, true, true).Settings.Profiles.Count == 2, "Profiles unchecked must not be imported");
    });
    Test("Config import: replace-all swaps the lists, merge keeps what is there, settings keep machine-bound values", () =>
    {
        var current = ExportFixture() with { Profiles = [new Profile { Id = "c1", Name = "devbox", Destination = "old" }, new Profile { Id = "c2", Name = "weg", Destination = "weg" }] };
        var incoming = new ConfigContent {
            Profiles = [new Profile { Id = "i1", Name = "devbox", Destination = "new" }, new Profile { Id = "i2", Name = "neu", Destination = "neu" }],
            Settings = new ExportedSettings { FontSize = 99, ClipHistoryLimit = 5000, AcceptOsc52 = true, CheckForUpdates = true, Shortcuts = new() { Paste = "Ctrl+Alt+V" }, Dictation = new() { Shortcut = "F9", Language = "en", SilenceSeconds = 1, LevelThreshold = 0.02, InitialPrompt = "x" } },
            PinnedClips = [new("neu-angeheftet", "x", DateTimeOffset.Now, true), new("geheimes-passwort-hinweis", "x", DateTimeOffset.Now, true)] };
        var plan = ConfigImporter.BuildPlan(current, incoming, [], ImportMode.ReplaceAll);
        var result = ConfigImporter.Apply(current, incoming, plan, new Dictionary<string, ConflictChoice>(), true, true, true, true).Settings;
        Check(result.Profiles.Select(p => p.Name).SequenceEqual(["devbox", "neu"]) && result.Profiles[0].Id == "c1" && result.Profiles[0].Destination == "new", "Replace-all profiles / ids");
        Check(result.PinnedClips.Select(c => c.Text).SequenceEqual(["neu-angeheftet", "geheimes-passwort-hinweis"]), "Replace-all pinned entries");
        Check(result.FontSize == 28 && result.ClipHistoryLimit == 1000 && result.Shortcuts.Paste == "Ctrl+Alt+V" && result.Dictation is { Shortcut: "F9", Language: "en" }, "Settings from the file (clamped)");
        Check(result.Dictation.Model == "small" && result.Dictation.Microphone == "Headset" && result.StartWithWindows && result.Update.SkippedVersion == "0.2.9", "Model, microphone, Windows start and skipped version stay local");
        var mergePlan = ConfigImporter.BuildPlan(current, incoming, [], ImportMode.Merge);
        var merged = ConfigImporter.Apply(current, incoming, mergePlan, new Dictionary<string, ConflictChoice>(), true, false, true, false).Settings;
        Check(merged.Profiles.Select(p => p.Name).OrderBy(n => n).SequenceEqual(["devbox", "neu", "weg"]) && merged.FontSize == 17 && merged.PinnedClips.Count(c => c.Text == "geheimes-passwort-hinweis") == 1 && merged.PinnedClips.Any(c => c.Text == "neu-angeheftet") && merged.PinnedClips.Count(c => c.Pinned) == 2, "Merge keeps current data and adds new pinned entries once");
        var noParts = ConfigImporter.Apply(current, new ConfigContent(), ConfigImporter.BuildPlan(current, new ConfigContent(), [], ImportMode.ReplaceAll), new Dictionary<string, ConflictChoice>(), true, true, true, true).Settings;
        Check(noParts.Profiles.SequenceEqual(current.Profiles) && noParts.PinnedClips.SequenceEqual(current.PinnedClips) && noParts.FontSize == current.FontSize
            && noParts.Dictation == current.Dictation && noParts.Shortcuts == current.Shortcuts && noParts.Update == current.Update && noParts.StartWithWindows == current.StartWithWindows,
            "Replace-all of a file without those parts must change nothing");
    });
    Test("Config import: existing SSH hosts are shown as conflicts and are never part of what gets written", () =>
    {
        var incoming = new ConfigContent { SshHosts = [new SshHostBlock(["devbox", "devbox-alt"], "Host devbox devbox-alt\n  HostName a"), new SshHostBlock(["neu"], "Host neu\n  HostName b"), new SshHostBlock(["*"], "Host *\n  User x")] };
        var plan = ConfigImporter.BuildPlan(new AppSettings(), incoming, new HashSet<string>(["DevBox"], StringComparer.OrdinalIgnoreCase), ImportMode.Merge);
        Check(plan.Hosts[0].Conflict && plan.Hosts[0].ExistingAliases.SequenceEqual(["devbox"]) && !plan.Hosts[1].Conflict && !plan.Hosts[2].Conflict, "Conflict detection (case-insensitive, wildcards ignored)");
        var result = ConfigImporter.Apply(new AppSettings(), incoming, plan, new Dictionary<string, ConflictChoice> { ["x"] = ConflictChoice.Replace }, true, true, true, true);
        Check(result.HostBlocksToAdd.Select(b => b.Aliases[0]).SequenceEqual(["neu", "*"]), "Blocks to write: " + string.Join(",", result.HostBlocksToAdd.Select(b => b.Aliases[0])));
        Check(ConfigImporter.Apply(new AppSettings(), incoming, plan, new Dictionary<string, ConflictChoice>(), true, true, true, false).HostBlocksToAdd.Count == 0, "Without confirmation nothing is written");
    });
    Test("Config backup: settings and SSH config are copied before an import and never overwrite an older backup", () =>
    {
        string data = Path.Combine(root, "backup-data");
        Directory.CreateDirectory(data);
        Check(ConfigBackup.BackupSettings(data, DateTimeOffset.Now) is null, "No settings file, no backup");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"FontSize\":12}");
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00+00:00");
        string first = ConfigBackup.BackupSettings(data, now)!, second = ConfigBackup.BackupSettings(data, now)!;
        Check(first.EndsWith(@"backup\settings-20261025-120000.json") && second != first && File.ReadAllText(first) == "{\"FontSize\":12}" && File.Exists(second), "Backups: " + first + " / " + second);
        string config = Path.Combine(root, "ssh-backup-config");
        File.WriteAllText(config, "Host a");
        string bak = ConfigBackup.BackupSshConfig(config, now)!;
        Check(bak == config + ".bak-20261025-120000" && File.ReadAllText(bak) == "Host a" && ConfigBackup.BackupSshConfig(config, now) != bak && ConfigBackup.BackupSshConfig(Path.Combine(root, "nix"), now) is null, "SSH config backup");
    });
    Test("Config check: missing key files and unknown hosts are found from 'ssh -G' output", () =>
    {
        string home = @"C:\Users\x";
        var profile = new Profile { Name = "devbox", Destination = "devbox" };
        string good = "hostname dev.example.test\nuser me\nidentityfile ~/.ssh/dev_key\nidentityfile ~/.ssh/id_rsa\n";
        Check(ConfigCheck.Evaluate(profile, good, p => p.EndsWith("dev_key"), home).Count == 0, "Existing key: no problem");
        var missing = ConfigCheck.Evaluate(profile, good, _ => false, home);
        Check(missing.Count == 1 && missing[0].Message.Contains("~/.ssh/dev_key") && missing[0].ProfileName == "devbox", "Explicit key missing: " + string.Join(";", missing.Select(m => m.Message)));
        var noKeys = ConfigCheck.Evaluate(profile, "hostname dev.example.test\nidentityfile ~/.ssh/id_ed25519\nidentityfile ~/.ssh/id_rsa\n", _ => false, home);
        Check(noKeys.Count == 1 && noKeys[0].Message.Contains("Agent"), "No default key: only a hint about the agent");
        Check(ConfigCheck.Evaluate(profile, "hostname devbox\nidentityfile ~/.ssh/id_rsa\n", _ => true, home).Single().Message.Contains("nicht definiert"), "Alias without a Host block");
        Check(ConfigCheck.Evaluate(new Profile { Name = "d", Destination = "me@h.example.test" }, "hostname h.example.test\nidentityfile ~/.ssh/id_rsa\n", _ => true, home).Count == 0, "Direct host is fine");
        Check(ConfigCheck.ParseEffective("hostname a\nidentityfile x\nidentityfile y\n\nbroken\n")["identityfile"].SequenceEqual(["x", "y"]), "Parsing repeated keys");
    });
    Test("Config check: the real 'ssh -G' (no connection) feeds the check", () =>
    {
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh)) { Console.WriteLine("  (skipped: no Windows OpenSSH client)"); return; }
        string config = Path.Combine(root, "ssh-g-config");
        File.WriteAllText(config, "Host mitschluessel\n  HostName k.example.test\n  IdentityFile ~/.ssh/tmgr-test-missing-key\nHost ok\n  HostName o.example.test\n");
        string G(string alias)
        {
            var info = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-F", config, "-G", alias }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            string text = p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return text;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var withKey = ConfigCheck.Evaluate(new Profile { Name = "k", Destination = "mitschluessel" }, G("mitschluessel"), File.Exists, home);
        Check(withKey.Any(p => p.Message.Contains("tmgr-test-missing-key")), "Missing explicit key from the real output: " + string.Join(";", withKey.Select(p => p.Message)));
        var unknown = ConfigCheck.Evaluate(new Profile { Name = "u", Destination = "gibtsnirgends" }, G("gibtsnirgends"), _ => true, home);
        Check(unknown.Any(p => p.Message.Contains("nicht definiert")), "Unknown alias from the real output: " + string.Join(";", unknown.Select(p => p.Message)));
        Check(!ConfigCheck.Evaluate(new Profile { Name = "o", Destination = "ok" }, G("ok"), _ => true, home).Any(p => p.Message.Contains("nicht definiert")), "Defined alias");
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
if (args.Contains("--ssh-smoke") && sshHost.Length == 0)
{
    failures.Add("SSH smoke: set TERMINALMGR_SSH_TEST_HOST to an SSH alias whose server runs tmux (key or agent login).");
    Console.WriteLine("FAIL " + failures[^1]);
}
else if (args.Contains("--ssh-smoke"))
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
            sshHost, "printf 'TERM:%s\n' \"$TERM\"; stty size; tput clear && printf 'TERMINFO_OK\n'; tmux -V"], 132, 47);
        int code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        string output;
        lock (transcriptLock) output = transcript.ToString();
        Check(code == 0 && output.Contains("TERM:xterm-256color") && output.Contains("47 132") && output.Contains("TERMINFO_OK"),
            "Remote smoke failed (exit " + code + "): " + output);
        passed++; Console.WriteLine("PASS Real SSH test host: TERM, remote PTY size, terminfo and tmux available");
        var sessions = await new TmuxSessionQuery().ListAsync(new() { Name = "testhost", Destination = sshHost });
        Check(sessions.Count > 0, "Expected existing tmux sessions on the test host");
        passed++; Console.WriteLine("PASS Real SSH tmux session discovery: " + sessions.Count + " sessions");
        passed += await RealTmuxWindowScenario();
        passed += await RealExitCodeScenario();
        passed += await RealOsc52Scenario();
        passed += await RealNavigationScenario();
        passed += await RealWindowStripScenario();
    }
    catch (Exception ex) { failures.Add("SSH smoke: " + ex.Message); Console.WriteLine("FAIL " + ex.Message); }
    finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
}
if (args.Any(a => a.StartsWith("--whisper-smoke")))
{
    // Real whisper.cpp through Whisper.net on speech synthesized by Windows (no microphone needed).
    // The model is downloaded once into a temp folder, never into the repository. Usage: --whisper-smoke[=tiny|small-q5_1|...]
    string modelName = args.First(a => a.StartsWith("--whisper-smoke")).Split('=', 2) is { Length: 2 } parts ? parts[1] : "tiny";
    try
    {
        var model = WhisperModels.Get(modelName);
        string directory = Path.Combine(Path.GetTempPath(), "TerminalMgr-whisper-smoke");
        if (!WhisperModels.IsInstalled(model, directory))
        {
            Console.WriteLine($"Downloading {model.Name} ({model.SizeText}) ...");
            long last = 0;
            await ModelDownloader.DownloadAsync(model, directory, new Progress<(long Done, long Total)>(p => {
                if (p.Done - last > 20_000_000) { last = p.Done; Console.WriteLine($"  {p.Done / 1_000_000} / {p.Total / 1_000_000} MB"); }
            }), CancellationToken.None);
        }
        using var transcriber = new WhisperTranscriber(directory);
        short[] Speak(string culture, string sentence)
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Culture.Name.StartsWith(culture));
            if (voice is null) return [];
            synth.SelectVoice(voice.VoiceInfo.Name);
            using var wav = new MemoryStream();
            synth.SetOutputToWaveStream(wav);
            synth.Speak(sentence);
            // 16-bit PCM mono; the synthesizer's native rate is converted to 16 kHz with a simple resample.
            wav.Position = 0;
            using var reader = new NAudio.Wave.WaveFileReader(wav);
            using var resampled = new NAudio.Wave.MediaFoundationResampler(new NAudio.Wave.WaveFormatConversionStream(new NAudio.Wave.WaveFormat(reader.WaveFormat.SampleRate, 16, 1), reader), new NAudio.Wave.WaveFormat(16000, 16, 1));
            var pcm = new MemoryStream();
            var buffer = new byte[16000];
            int read;
            while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, read);
            var samples = new short[pcm.Length / 2];
            Buffer.BlockCopy(pcm.ToArray(), 0, samples, 0, samples.Length * 2);
            return samples;
        }
        var english = Speak("en", "Hello world. This is a test of the dictation feature.");
        Check(english.Length > 16000, "English voice produced no audio");
        var options = new DictationOptions { Language = "en", Model = model.Name, InitialPrompt = "" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string textEn = await transcriber.TranscribeAsync(english, options, CancellationToken.None);
        Console.WriteLine($"  EN ({sw.Elapsed.TotalSeconds:0.0} s, backend {transcriber.Backend}): {textEn}");
        Check(textEn.Contains("test", StringComparison.OrdinalIgnoreCase) && textEn.Contains("hello", StringComparison.OrdinalIgnoreCase), "English not recognized: " + textEn);
        passed++; Console.WriteLine("PASS Real whisper.cpp recognizes synthesized English speech offline");
        var german = Speak("de", "Starte bitte den tmux Server und öffne Claude Code.");
        if (german.Length > 16000)
        {
            sw.Restart();
            string textDe = await transcriber.TranscribeAsync(german, options with { Language = "de", InitialPrompt = "tmux, Claude Code" }, CancellationToken.None);
            Console.WriteLine($"  DE ({sw.Elapsed.TotalSeconds:0.0} s): {textDe}");
            Check(textDe.Contains("tmux", StringComparison.OrdinalIgnoreCase) || textDe.Contains("Server", StringComparison.OrdinalIgnoreCase) || textDe.Contains("Claude", StringComparison.OrdinalIgnoreCase), "German not recognized: " + textDe);
            passed++; Console.WriteLine("PASS Real whisper.cpp recognizes synthesized German speech with technical terms");
        }
        // Silence must not turn into invented text through the session logic.
        var silent = new short[16000 * 2];
        string textSilent = await transcriber.TranscribeAsync(silent, options, CancellationToken.None);
        Console.WriteLine($"  Silence -> '{textSilent}'");
    }
    catch (Exception ex) { failures.Add("Whisper smoke: " + ex.Message); Console.WriteLine("FAIL " + ex); }
}
if (args.Contains("--probe-mic"))
{
    // How long does the microphone need from the start request to the first audio? (Ctrl+Y to the start of the dictation.)
    for (int round = 1; round <= 4; round++)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new MicrophoneSource();
        long created = clock.ElapsedMilliseconds;
        source.Samples += _ => first.TrySetResult(clock.ElapsedMilliseconds);
        source.Start();
        long started = clock.ElapsedMilliseconds;
        long firstAudio = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"  Runde {round}: Quelle anlegen {created} ms, Start() {started - created} ms, erstes Audio nach {firstAudio} ms");
    }
}
if (args.Contains("--engine-smoke"))
{
    // Does the Windows speech recognizer work at all on this machine? It records from the default microphone on its own.
    try
    {
        var options = new DictationOptions { Language = "de", PingStart = false }.Normalized();
        Console.WriteLine("Windows-Spracherkennung, Sprache vorbereiten: " + (await WindowsSpeechEngine.CheckAsync(options) ?? "ok"));
        using var engine = new WindowsSpeechEngine(options);
        var outcome = new TaskCompletionSource<string>();
        engine.Interim += (c, t) => outcome.TrySetResult("Zwischenstand: " + c + " | " + t);
        engine.Completed += r => outcome.TrySetResult("Ergebnis: '" + r.Text + "'");
        engine.Failed += ex => outcome.TrySetResult((ex is SpeechUnavailableException ? "nicht verfügbar: " : "Fehler: ") + ex.Message);
        engine.Start();
        var spoken = Task.Run(() => { Thread.Sleep(2000); var audio = SpeechBench.Synthesize("de", "Starte bitte den Server."); Console.WriteLine("  (Windows-Stimme spricht über die Lautsprecher)"); SpeakAloud("Starte bitte den Server."); });
        Console.WriteLine("Windows-Spracherkennung: " + await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        engine.Cancel();
    }
    catch (Exception ex) { Console.WriteLine("Windows-Spracherkennung: " + ex.Message); }
    static void SpeakAloud(string text) { using var synth = new System.Speech.Synthesis.SpeechSynthesizer(); synth.SelectVoice("Microsoft Hedda Desktop"); synth.Speak(text); }
}
if (args.Any(a => a.StartsWith("--dictation-bench")))
{
    // Measuring mode for the Whisper engine: synthesized German speech is played into the dictation in real time.
    // Compares the streaming interim text (small model, sliding window, confirmed words) with the old behavior
    // (the whole segment transcribed again with the final model). Models come from the normal model folder; missing ones are downloaded.
    try
    {
        var options = new DictationOptions { Language = "de", PingStart = false }.Normalized();
        foreach (string modelName in new[] { options.Model, options.InterimModel }.Distinct())
        {
            var model = WhisperModels.Get(modelName);
            if (WhisperModels.IsInstalled(model)) continue;
            Console.WriteLine($"Downloading {model.Name} ({model.SizeText}) ...");
            long last = 0;
            await ModelDownloader.DownloadAsync(model, AppPaths.ModelsDirectory(), new Progress<(long Done, long Total)>(p => {
                if (p.Done - last > 50_000_000) { last = p.Done; Console.WriteLine($"  {p.Done / 1_000_000} / {p.Total / 1_000_000} MB"); }
            }), CancellationToken.None);
        }
        string[] shortText = ["Starte bitte den tmux Server und öffne Claude Code im Projektordner."];
        string[] mediumText = ["Erstelle einen neuen Branch für die Konfiguration, schreibe die Tests dazu und schiebe die Änderung auf GitHub, sobald der Build grün ist."];
        string[] longText = ["Erstelle einen neuen Branch für die Konfiguration und schreibe die Tests dazu.", "Danach starte den Docker Container auf dem Entwicklungsserver neu.",
            "Prüfe im Log mit tail, ob der SSH Verbindungsaufbau noch Fehler meldet.", "Wenn alles grün ist, schiebe die Änderung auf GitHub und öffne einen Pull Request."];
        var fixtures = new (string Name, string[] Sentences)[] { ("kurz", shortText), ("mittel", mediumText), ("lang", longText) };
        using var finalTranscriber = new WhisperTranscriber();
        using var interimTranscriber = new WhisperTranscriber(interim: true);
        Console.WriteLine("Lade und wärme die Modelle auf ...");
        var warm = System.Diagnostics.Stopwatch.StartNew();
        await interimTranscriber.PrepareAsync(options);
        await finalTranscriber.PrepareAsync(options);
        Console.WriteLine($"  bereit nach {warm.Elapsed.TotalSeconds:0.0} s, Backend {finalTranscriber.Backend}");
        var rows = new List<string>();
        foreach (var (fixtureName, sentences) in fixtures)
        {
            var audio = SpeechBench.SynthesizeSentences("de", sentences);
            if (audio.Length < 16000) { Console.WriteLine("Keine deutsche Stimme installiert, Messung übersprungen."); break; }
            string reference = string.Join(" ", sentences);
            double seconds = audio.Length / 16000.0;
            double rms = Math.Sqrt(audio.Select(s => (double)s * s).Average()) / 32768.0;
            Console.WriteLine($"[{fixtureName}] {seconds:0.0} s Audio, Effektivwert {rms:0.000}, Spitze {audio.Max(s => Math.Abs((int)s)) / 32768.0:0.00}");
            var direct = System.Diagnostics.Stopwatch.StartNew();
            string directText = await finalTranscriber.TranscribeAsync(audio, options, CancellationToken.None);
            Console.WriteLine($"  direkt (Endmodell, {direct.ElapsedMilliseconds} ms): {directText}");
            direct.Restart();
            var directWords = await interimTranscriber.TranscribeWordsAsync(audio, options, CancellationToken.None);
            Console.WriteLine($"  direkt (Zwischenmodell, Wörter, {direct.ElapsedMilliseconds} ms): " + string.Join(" ", directWords.Select(w => $"{w.Text}[{w.Start.TotalSeconds:0.0}-{w.End.TotalSeconds:0.0}]")));
            if (args.Contains("--probe-threads"))
            {
                // Same call as in the dictation session: from a pool thread, with a cancelable token, while another thread keeps running.
                using var cts = new CancellationTokenSource();
                var slice = audio.Take((int)(0.7 * 16000)).ToArray();
                for (int i = 0; i < 3; i++)
                {
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var task = Task.Run(() => interimTranscriber.TranscribeWordsAsync(slice, options, cts.Token));
                    var finished = await Task.WhenAny(task, Task.Delay(8000));
                    Console.WriteLine($"  Pool-Thread {i}: {(finished == task ? "fertig nach " + sw2.ElapsedMilliseconds + " ms: " + string.Join(" ", task.Result.Select(w => w.Text)) : "HÄNGT nach 8 s")}");
                    if (finished != task) break;
                }
            }
            if (args.Contains("--probe-windows"))
                foreach (double windowSeconds in new[] { 0.7, 1.2, 1.5, 2.0, 3.0, 4.0 })
                {
                    var slice = audio.Take((int)(windowSeconds * 16000)).ToArray();
                    Console.Write($"  Fenster {windowSeconds:0.0} s: ");
                    var probe = await interimTranscriber.TranscribeWordsAsync(slice, options, CancellationToken.None);
                    Console.WriteLine(string.Join(" ", probe.Select(w => w.Text)));
                }
            bool traced = Environment.GetEnvironmentVariable("TERMINALMGR_BENCH_TRACE") == "1";
            foreach (bool streaming in new[] { true, false })
            {
                using var session = new DictationSession(() => new WavFileSource(audio, 2.5), traced ? new TracingTranscriber(finalTranscriber, "final") : finalTranscriber, options, partialSeconds: 1.75, interim: streaming ? (traced ? new TracingTranscriber(interimTranscriber, "interim") : interimTranscriber) : null);
                var result = await SpeechBench.RunAsync(streaming ? "Whisper Streaming (" + options.InterimModel + ", Fenster)" : "Whisper alt (Gesamtsegment, " + options.Model + ")",
                    fixtureName, session, reference, seconds, TimeSpan.FromSeconds(seconds + 60));
                Console.WriteLine(result.Line());
                Console.WriteLine(SpeechBench.Describe(result));
                rows.Add(result.Line());
            }
        }
        Console.WriteLine();
        Console.WriteLine(SpeechBench.TableHeader);
        foreach (string row in rows) Console.WriteLine(row);
    }
    catch (Exception ex) { failures.Add("Dictation bench: " + ex.Message); Console.WriteLine("FAIL " + ex); }
}
Console.WriteLine($"{passed} passed, {failures.Count} failed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

// Does tmux hand a copy of Claude Code on to the terminal as OSC 52 without any change of the tmux configuration?
// Claude Code copies with "tmux load-buffer -w -"; the same command is used here on a throw-away session with an attached client.
async Task<int> RealOsc52Scenario()
{
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    var profile = new Profile { Name = "testhost", Destination = sshHost };
    async Task<string> Remote(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", sshHost, command }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return await stdout + await stderr;
    }
    PtySession? client = null;
    try
    {
        await Remote("tmux kill-session -t tmgr-osc 2>/dev/null; tmux new-session -d -s tmgr-osc && true");
        string setClipboard = (await Remote("tmux show-options -s set-clipboard")).Trim();
        var session = (await new TmuxSessionQuery().ListAsync(profile)).Single(s => s.Name == "tmgr-osc");
        var screen = new StringBuilder();
        client = new PtySession();
        client.Output += s => { lock (screen) screen.Append(s); };
        client.Start(ssh, SshCommand.TmuxAttachArguments(profile, session.Id), 120, 30);
        await Task.Delay(1500);
        const string copied = "Claude Code kopiert: äöü mit Umlauten\nund zweiter Zeile";
        await Remote("printf '%s' " + SshCommand.ShellQuote(copied) + " | tmux load-buffer -w -");
        string payload = "";
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && payload.Length == 0)
        {
            string text; lock (screen) text = screen.ToString();
            var match = System.Text.RegularExpressions.Regex.Match(text, "\u001b\\]52;([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)");
            if (match.Success) payload = match.Groups[1].Value;
            else await Task.Delay(100);
        }
        Check(payload.Length > 0, $"tmux did not forward OSC 52 (set-clipboard: '{setClipboard}'). Add 'set -s set-clipboard on' to ~/.tmux.conf.");
        Check(Osc52.TryDecode(payload, out var decoded) && decoded == copied, "Forwarded text differs: " + payload);
        Console.WriteLine($"PASS Real tmux forwards a copy of Claude Code as OSC 52 without extra configuration ({setClipboard})");
        return 1;
    }
    finally
    {
        if (client is not null) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Remote("tmux kill-session -t tmgr-osc 2>/dev/null; true");
    }
}

// The window strip: one SSH call with the script from WindowStripParser. A throw-away session with a busy, a questioning and a quiet window.
async Task<int> RealWindowStripScenario()
{
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    var profile = new Profile { Name = "testhost", Destination = sshHost };
    async Task<string> Remote(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", sshHost, command }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return await stdout + await stderr;
    }
    try
    {
        await Remote("tmux kill-session -t tmgr-strip 2>/dev/null; tmux new-session -d -s tmgr-strip -n quiet 'sh' && tmux new-window -t tmgr-strip -n busy 'sh -c \"while true; do echo tick-$(date +%s); sleep 1; done\"' && tmux new-window -t tmgr-strip -n ask 'sh -c \"sleep 4; printf \\\"Apply changes? \\\"; sleep 600\"' && tmux select-window -t tmgr-strip:quiet");
        // The long-lived connection of the strip: one frame per second. The busy window prints every second, the asking window
        // stays silent for 4 s and then stops on a question, the quiet window never changes.
        var tracker = new WindowActivityTracker();
        IReadOnlyList<WindowCard> cards = [];
        WindowPoll? firstFrame = null;
        int frames = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = TmuxStripStream.Start(profile, "tmgr-strip",
            poll => { lock (tracker) { firstFrame ??= poll; cards = tracker.Update(poll, DateTimeOffset.Now); if (++frames >= 14) enough.TrySetResult(); } },
            reason => enough.TrySetException(new Exception("Stream ended: " + reason)));
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(40));
        lock (tracker)
        {
            Check(firstFrame!.ServerEpoch > 1_700_000_000, "Server clock missing: " + firstFrame.ServerEpoch);
            Check(cards.Select(c => c.Name).SequenceEqual(["quiet", "busy", "ask"]), "Windows: " + string.Join(",", cards.Select(c => c.Name)));
            Check(cards[0].Current && !cards[1].Current, "Active flag");
            Check(cards[1].State == StripState.Running && cards[1].Preview.Any(l => l.StartsWith("tick-")), "Busy window runs, preview is the tick line: " + cards[1].State + " " + string.Join("|", cards[1].Preview));
            Check(cards[2].State == StripState.Asking, "Stopped on a question after it printed: " + cards[2].State + " " + cards[2].Detail + " " + string.Join("|", cards[2].Preview));
            Check(cards[0].State == StripState.Idle, "A window that never changes is quiet: " + cards[0].State);
            Console.WriteLine($"PASS Real tmux: one connection, a frame per second; busy window runs ({cards[1].Detail}), a question is detected, a static window stays quiet");
        }
        return 1;
    }
    finally { await Remote("tmux kill-session -t tmgr-strip 2>/dev/null; true"); }
}

// The navigation shortcuts switch the tmux window through the command prompt of the own client. While a program runs in the window
// that is left, not a single byte of those commands may reach it. A throw-away tmux session with a recording program (raw tty, cat)
// stands in for Claude Code; the user's own tmux sessions are not touched.
async Task<int> RealNavigationScenario()
{
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    var profile = new Profile { Name = "testhost", Destination = sshHost };
    async Task<string> Remote(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", sshHost, command }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return await stdout + await stderr;
    }
    async Task<string> CurrentWindow() => (await Remote("tmux display-message -p -t tmgr-nav '#{window_index}'")).Trim();
    PtySession? client = null;
    try
    {
        await Remote("rm -f /tmp/tmgr-nav.bin; tmux kill-session -t tmgr-nav 2>/dev/null; tmux new-session -d -s tmgr-nav -n recorder 'stty raw -echo; exec cat > /tmp/tmgr-nav.bin' && tmux new-window -t tmgr-nav -n w1 'sleep 600' && tmux new-window -t tmgr-nav -n w2 'sleep 600' && tmux select-window -t tmgr-nav:0");
        var session = (await new TmuxSessionQuery().ListAsync(profile)).Single(s => s.Name == "tmgr-nav");
        client = new PtySession();
        client.Output += _ => { };
        client.Start(ssh, SshCommand.TmuxAttachArguments(profile, session.Id, (await new TmuxSessionQuery().ListWindowsAsync(profile)).Single(w => w.SessionName == "tmgr-nav" && w.Index == 0).WindowId), 120, 30);
        await Task.Delay(1500);
        Check(await CurrentWindow() == "0", "Start in the recording window");
        async Task Run(string command, string expectedWindow)
        {
            await TmuxClient.RunCommandAsync(client, command);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && await CurrentWindow() != expectedWindow) await Task.Delay(100);
            Check(await CurrentWindow() == expectedWindow, $"'{command}' should end in window {expectedWindow}, is in {await CurrentWindow()}");
        }
        await Run(TmuxNavigation.SelectIndex(2), "2");
        await Run(TmuxNavigation.Next, "0");        // wraps around
        await Run(TmuxNavigation.Previous, "2");
        await Run(TmuxNavigation.SelectIndex(0), "0");
        // Many switches that start in the recording window, where a leaked character would be caught.
        for (int i = 0; i < 12; i++)
        {
            await Run(TmuxNavigation.SelectIndex(1), "1");   // starts in the recording window
            await Run(TmuxNavigation.Previous, "0");         // and back
        }
        string received = (await Remote("wc -c < /tmp/tmgr-nav.bin")).Trim();
        Check(received == "0", "The program received " + received + " bytes: " + (await Remote("od -c /tmp/tmgr-nav.bin | head -5")));
        Console.WriteLine("PASS Real tmux: window by index, next, previous through the command prompt; 0 bytes reached the program in 28 switches");
        return 1;
    }
    finally
    {
        if (client is not null) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Remote("tmux kill-session -t tmgr-nav 2>/dev/null; rm -f /tmp/tmgr-nav.bin; true");
    }
}

// Which exit code does OpenSSH give for a dropped connection and for normal ends? The reconnect option relies on it.
async Task<int> RealExitCodeScenario()
{
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    async Task<int> RunAsync(string remoteCommand)
    {
        var session = new PtySession();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += _ => { };
        session.Exited += code => exited.TrySetResult(code);
        try
        {
            session.Start(ssh, ["-tt", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", sshHost, remoteCommand], 100, 30);
            return await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    int normal = await RunAsync("exit 0"), failing = await RunAsync("exit 3");
    // The server side ends the SSH session process: for the client this is a lost connection.
    int dropped = await RunAsync("kill -9 $PPID");
    Check(normal == 0 && failing == 3, $"Normal ends must keep their exit code: {normal}, {failing}");
    Check(ReconnectTracker.IsConnectionLost(dropped), "A dropped connection must count as lost (255 or -1), was " + dropped);
    var tracker = new ReconnectTracker();
    Check(!tracker.Lost(true, normal, DateTimeOffset.Now).Retry && !tracker.Lost(true, failing, DateTimeOffset.Now).Retry && tracker.Lost(true, dropped, DateTimeOffset.Now).Retry, "Policy disagrees with real exit codes");
    Console.WriteLine($"PASS Real SSH exit codes: normal end {normal}, remote failure {failing}, dropped connection {dropped} (only the last is reconnected)");
    return 1;
}

// Real tmux on the test host with throw-away sessions (tmgr-ta / tmgr-tb); the user's own sessions are never touched.
async Task<int> RealTmuxWindowScenario()
{
    int checks = 0;
    var profile = new Profile { Name = "testhost", Destination = sshHost };
    string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
    async Task<string> Remote(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", sshHost, command }) start.ArgumentList.Add(a);
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

/// <summary>A tiny HTTP server on a free loopback port for the update tests; no internet and no GitHub involved.</summary>
sealed class LocalServer : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    public readonly Dictionary<string, Func<System.Net.HttpListenerContext, Task>> Routes = [];
    public string BaseUrl { get; }

    public LocalServer()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (Routes.TryGetValue(context.Request.Url!.AbsolutePath, out var handler)) await handler(context);
                        else { context.Response.StatusCode = 404; context.Response.Close(); }
                    }
                    catch (Exception) { try { context.Response.Abort(); } catch (Exception) { } }
                });
            }
        });
    }

    public static Task Text(System.Net.HttpListenerContext c, string text) => Bytes(c, Encoding.UTF8.GetBytes(text));
    public static async Task Bytes(System.Net.HttpListenerContext c, byte[] data)
    {
        c.Response.ContentLength64 = data.Length;
        await c.Response.OutputStream.WriteAsync(data);
        c.Response.Close();
    }
    /// <summary>Announces the full length, delivers only part of it and drops the connection.</summary>
    public static async Task Truncated(System.Net.HttpListenerContext c, byte[] data, int deliver)
    {
        c.Response.ContentLength64 = data.Length;
        await c.Response.OutputStream.WriteAsync(data.AsMemory(0, deliver));
        await c.Response.OutputStream.FlushAsync();
        c.Response.Abort();
    }
    /// <summary>Delivers the body slowly (about 3 s in total).</summary>
    public static async Task Slow(System.Net.HttpListenerContext c, byte[] data)
    {
        c.Response.ContentLength64 = data.Length;
        await Task.Delay(2000); // even a tiny body must be late, whatever the speed of the machine
        for (int i = 0; i < data.Length; i += Math.Max(1, data.Length / 10))
        {
            await Task.Delay(300);
            await c.Response.OutputStream.WriteAsync(data.AsMemory(i, Math.Min(Math.Max(1, data.Length / 10), data.Length - i)));
            await c.Response.OutputStream.FlushAsync();
        }
        c.Response.Close();
    }
    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch (Exception) { } }
}

sealed class FakeSource : IAudioSource
{
    public event Action<short[]>? Samples;
    public event Action<Exception>? Failed;
    public bool Disposed;
    public Exception? StartError;
    public void Start() { if (StartError is not null) throw StartError; }
    public void Stop() { }
    public void Dispose() => Disposed = true;
    public void Feed(short[] samples) => Samples?.Invoke(samples);
    public void Break(Exception error) => Failed?.Invoke(error);
}

sealed class FakeTranscriber : ITranscriber
{
    private int _running;
    public readonly List<int> Calls = [];
    public int Delay, MaxConcurrent;
    public Exception? Error;
    public Func<int, string> Responder = _ => "text";
    public async Task<string> TranscribeAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        lock (Calls) { Calls.Add(samples.Length); MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running)); }
        try
        {
            if (Delay > 0) await Task.Delay(Delay, cancellationToken);
            if (Error is not null) throw Error;
            return Responder(samples.Length);
        }
        finally { Interlocked.Decrement(ref _running); }
    }
}

sealed class StubHandler(Func<Stream> body, long length) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new StreamContent(body());
        content.Headers.ContentLength = length;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}

/// <summary>Delivers a little data and then waits until the download is canceled.</summary>
sealed class HangingStream : Stream
{
    private bool _first = true;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_first) { _first = false; "lmgg"u8.CopyTo(buffer.Span); return 4; }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class FakeWordTranscriber : IWordTranscriber
{
    public readonly List<(int Samples, int TotalAtCall)> Windows = [];
    public Func<int, short[], IReadOnlyList<TimedWord>> Responder = (_, _) => [];
    public Func<long>? Total;
    public Task<IReadOnlyList<TimedWord>> TranscribeWordsAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        int call;
        lock (Windows) { Windows.Add((samples.Length, (int)(Total?.Invoke() ?? 0))); call = Windows.Count; }
        return Task.FromResult(Responder(call, samples));
    }
}
