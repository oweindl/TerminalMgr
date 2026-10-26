using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

public enum StripState { Idle, Running, Done, Asking }

/// <summary>What one frame of tmux says about one window: its flags and everything visible on its screen.</summary>
public sealed record WindowSnapshot(
    string WindowId, int Index, string Name, string Command, bool Active, bool Bell, bool Dead, int? ExitStatus,
    IReadOnlyList<string> Screen);

/// <summary>A window as the strip shows it.</summary>
public sealed record WindowCard(
    string WindowId, int Index, string Name, string Command, bool Current, StripState State, string StateLabel, string Detail,
    IReadOnlyList<string> Preview, IReadOnlyList<double> Spark)
{
    public string Shortcut => Index <= 9 ? "Ctrl " + Index : "";
}

/// <summary>One frame: the server's clock and one snapshot per window.</summary>
public sealed record WindowPoll(long ServerEpoch, IReadOnlyList<WindowSnapshot> Windows);

public static class WindowStripParser
{
    public const string Format = "#{window_id}\t#{window_index}\t#{window_active}\t#{window_bell_flag}\t#{pane_dead}\t#{pane_dead_status}\t#{pane_current_command}\t#{window_name}";
    public const string NowMarker = "@@NOW ";
    public const string ScreenMarker = "@@W ";
    public const string EndMarker = "@@END";
    public const int ScreenLines = 60;
    public const int ScreenWidth = 200;

    /// <summary>
    /// Shell script for the server: the clock, the windows of one session, then the visible screen of each window, one frame per
    /// <paramref name="loop"/> second (0 = a single frame). Plain sh, so it works whatever the login shell is. When the SSH connection
    /// ends, the loop ends with it.
    /// </summary>
    public static string Script(string sessionTarget, int loop = 0)
    {
        string t = SshCommand.ShellQuote(sessionTarget);
        string frame = $"echo '{NowMarker}'$(date +%s); tmux list-windows -t {t} -F {SshCommand.ShellQuote(Format)} || exit 1; " +
                       $"for w in $(tmux list-windows -t {t} -F '#{{window_id}}'); do echo '{ScreenMarker}'$w; " +
                       $"tmux capture-pane -p -t \"$w\" | tail -n {ScreenLines} | cut -c1-{ScreenWidth}; done; echo '{EndMarker}'";
        return loop <= 0 ? frame : $"while :; do {frame}; sleep {loop}; done";
    }

    public static WindowPoll Parse(string output)
    {
        long now = 0;
        var records = new List<(string Id, int Index, string Name, string Command, bool Active, bool Bell, bool Dead, int? Exit)>();
        var screens = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(NowMarker, StringComparison.Ordinal)) { long.TryParse(line[NowMarker.Length..].Trim(), out now); continue; }
            if (line.StartsWith(ScreenMarker, StringComparison.Ordinal))
            {
                string id = line[ScreenMarker.Length..].Trim();
                current = TmuxWindowParser.IsWindowId(id) ? screens[id] = [] : null;
                continue;
            }
            if (line == EndMarker) { current = null; continue; }
            if (current is not null) { current.Add(Clean(line)); continue; }
            string[] f = line.Split('\t');
            if (f.Length < 8 || !TmuxWindowParser.IsWindowId(f[0]) || !int.TryParse(f[1], out int index) || index < 0) continue;
            int? exit = f[5].Length > 0 && int.TryParse(f[5], out int code) ? code : null;
            records.Add((f[0], index, string.Join('\t', f[7..]), f[6], f[2] == "1", f[3] == "1", f[4] == "1", exit));
        }
        var windows = records.OrderBy(r => r.Index).Select(r => new WindowSnapshot(
            r.Id, r.Index, r.Name, r.Command, r.Active, r.Bell, r.Dead, r.Exit,
            screens.TryGetValue(r.Id, out var s) ? s : [])).ToList();
        return new WindowPoll(now, windows);
    }

    /// <summary>The screen is remote text: no control characters, no escape sequences, tabs as spaces, no trailing blanks.</summary>
    private static string Clean(string line)
    {
        string text = Regex.Replace(line, "\u001b\\[[0-9;?]*[ -/]*[@-~]", "").Replace('\t', ' ');
        return new string(text.Where(c => !char.IsControl(c)).ToArray()).TrimEnd();
    }
}

/// <summary>
/// Turns frames into cards. A window "runs" while its screen keeps changing and is "done" when it has stopped (after it ran);
/// the preview is not the bottom of the screen but the lines that appeared most recently, so a static input line or status bar
/// does not hide what is happening above it. Nothing here depends on the tmux activity timestamp, which also moves for a cursor or a clock.
/// </summary>
public sealed class WindowActivityTracker
{
    /// <summary>A window stays "running" for this long after its last screen change (the frames come once a second).</summary>
    public const int RunningHoldSeconds = 3;
    public const int SparkBuckets = 8;
    public const int BucketSeconds = 5;
    private const int RecentLines = 12;
    private const int QuestionTail = 8;
    private static readonly Regex Question = new(
        @"(\?\s*$)|(\[[yY]/[nN]\]\s*:?\s*$)|(\([yY](es)?/[nN](o)?\)\s*:?\s*$)|(yes/no\)?\s*:?\s*$)|(password[^:]*:\s*$)|(press enter)|(continue\?)|(do you want to)|(❯\s*1\.)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Digits = new("[0-9]+", RegexOptions.Compiled);

    private sealed class Memory
    {
        public List<string>? Previous;
        public DateTimeOffset? LastChange;
        public DateTimeOffset? RunningSince;
        public DateTimeOffset? DoneAt;
        public DateTimeOffset? AskingSince;
        public bool WasActive;
        public readonly List<(string Key, string Text, DateTimeOffset At)> Recent = [];
        public readonly List<double> Spark = [];
        public DateTimeOffset BucketStart;
        public double BucketValue;
    }

    private readonly Dictionary<string, Memory> _memory = [];

    public static bool LooksLikeQuestion(string? line) => !string.IsNullOrWhiteSpace(line) && Question.IsMatch(line);

    /// <summary>A line that says something: at least one letter or digit (not just a frame of box-drawing characters).</summary>
    public static bool HasContent(string line) => line.Any(char.IsLetterOrDigit);

    /// <summary>Forget the previous screens, e.g. after the connection to the server was lost: the next frame is a new baseline.</summary>
    public void Reset() { foreach (var m in _memory.Values) m.Previous = null; }

    /// <summary>
    /// The lines of <paramref name="current"/> that were not on the screen before. Lines that only moved (the text scrolled)
    /// are not new; lines that did not move at all (input box, status bar) are not new either.
    /// </summary>
    public static List<string> NewLines(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var available = new Dictionary<string, int>();
        foreach (string line in previous) if (HasContent(line)) available[line] = available.GetValueOrDefault(line) + 1;
        var fresh = new List<string>();
        foreach (string line in current)
        {
            if (!HasContent(line)) continue;
            if (available.TryGetValue(line, out int n) && n > 0) available[line] = n - 1;
            else fresh.Add(line);
        }
        return fresh;
    }

    public IReadOnlyList<WindowCard> Update(WindowPoll poll, DateTimeOffset now)
    {
        var live = poll.Windows.Select(w => w.WindowId).ToHashSet();
        foreach (string gone in _memory.Keys.Where(k => !live.Contains(k)).ToList()) _memory.Remove(gone);
        var cards = new List<WindowCard>();
        foreach (var w in poll.Windows)
        {
            if (!_memory.TryGetValue(w.WindowId, out var m)) _memory[w.WindowId] = m = new Memory { BucketStart = now };
            var screen = w.Screen.ToList();
            double intensity = 0;
            if (m.Previous is not null && !screen.SequenceEqual(m.Previous))
            {
                m.LastChange = now;
                var fresh = NewLines(m.Previous, screen);
                foreach (string line in fresh)
                {
                    string key = Digits.Replace(line.Trim(), "#");   // a counter or timer line replaces its older self
                    m.Recent.RemoveAll(r => r.Key == key);
                    m.Recent.Add((key, line.Trim(), now));
                }
                if (m.Recent.Count > RecentLines) m.Recent.RemoveRange(0, m.Recent.Count - RecentLines);
                intensity = Math.Min(1, 0.25 + fresh.Count / 3.0);
            }
            m.Previous = screen;
            AddToSpark(m, intensity, now);

            bool running = !w.Dead && m.LastChange is { } last && (now - last).TotalSeconds <= RunningHoldSeconds;
            if (w.Active && !m.WasActive) { m.DoneAt = null; m.AskingSince = null; }   // switched to the window: it has been looked at
            m.WasActive = w.Active;
            StripState state;
            if (w.Dead) { m.RunningSince = null; m.DoneAt ??= now; state = StripState.Done; }
            else if (w.Bell && !w.Active) { m.RunningSince = null; m.AskingSince ??= now; state = StripState.Asking; }
            else if (running)
            {
                m.RunningSince ??= m.LastChange;
                m.DoneAt = null; m.AskingSince = null;
                state = StripState.Running;
            }
            else
            {
                if (m.RunningSince is not null)
                {
                    // It was busy and has stopped: waiting for an answer, or finished.
                    m.RunningSince = null;
                    if (screen.Where(HasContent).TakeLast(QuestionTail).Any(LooksLikeQuestion)) m.AskingSince = now; else m.DoneAt = m.LastChange ?? now;
                }
                state = m.AskingSince is not null ? StripState.Asking : m.DoneAt is not null ? StripState.Done : StripState.Idle;
            }
            cards.Add(new WindowCard(w.WindowId, w.Index, w.Name, w.Command, w.Active, state, Label(state), Detail(w, state, m, now),
                Preview(m, screen), SparkValues(m)));
        }
        return cards;
    }

    /// <summary>The two lines that appeared last (older first); a window that never changed shows the last two lines on its screen.</summary>
    private static List<string> Preview(Memory m, List<string> screen)
    {
        var recent = m.Recent.TakeLast(2).Select(r => r.Text).ToList();
        return recent.Count > 0 ? recent : screen.Where(HasContent).TakeLast(2).Select(l => l.Trim()).ToList();
    }

    private static void AddToSpark(Memory m, double intensity, DateTimeOffset now)
    {
        m.BucketValue = Math.Max(m.BucketValue, intensity);
        if ((now - m.BucketStart).TotalSeconds < BucketSeconds) return;
        m.Spark.Add(m.BucketValue);
        if (m.Spark.Count > SparkBuckets) m.Spark.RemoveAt(0);
        m.BucketStart = now;
        m.BucketValue = 0;
    }

    private static List<double> SparkValues(Memory m)
    {
        var values = m.Spark.Concat([m.BucketValue]).TakeLast(SparkBuckets).ToList();
        return values;
    }

    private static string Label(StripState state) => state switch
    {
        StripState.Running => "läuft", StripState.Done => "fertig", StripState.Asking => "fragt", _ => "ruhig"
    };

    private static string Detail(WindowSnapshot w, StripState state, Memory m, DateTimeOffset now)
    {
        string time = state switch
        {
            StripState.Running => m.RunningSince is { } s ? Duration((long)(now - s).TotalSeconds) : "gerade",
            StripState.Asking => m.AskingSince is { } a ? "wartet " + Duration((long)(now - a).TotalSeconds) : "wartet",
            StripState.Done => (w.Dead && w.ExitStatus is { } code ? $"exit {code} · " : "") + (m.DoneAt is { } d ? "vor " + Ago((long)(now - d).TotalSeconds) : "vor kurzem"),
            _ => m.LastChange is { } c ? Ago((long)(now - c).TotalSeconds) : ""
        };
        return w.Command.Length > 0 && time.Length > 0 ? w.Command + " · " + time : w.Command + time;
    }

    /// <summary>m:ss for the time something has been running.</summary>
    public static string Duration(long seconds) => seconds < 3600 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds / 3600} h {seconds % 3600 / 60} min";

    /// <summary>German short form for "how long ago".</summary>
    public static string Ago(long seconds) =>
        seconds < 45 ? "gerade" : seconds < 3600 ? $"{Math.Max(1, (seconds + 30) / 60)} min" : seconds < 86400 ? $"{seconds / 3600} h" : $"{seconds / 86400} d";
}
