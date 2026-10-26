using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

public enum StripState { Idle, Running, Done, Asking }

/// <summary>What one poll of tmux says about one window.</summary>
public sealed record WindowSnapshot(
    string WindowId, int Index, string Name, string Command, bool Active, bool Bell, long ActivityEpoch, bool Dead, int? ExitStatus,
    IReadOnlyList<string> Preview);

/// <summary>A window as the strip shows it.</summary>
public sealed record WindowCard(
    string WindowId, int Index, string Name, string Command, bool Current, StripState State, string StateLabel, string Detail,
    IReadOnlyList<string> Preview, IReadOnlyList<double> Spark)
{
    public string Shortcut => Index <= 9 ? "Ctrl " + Index : "";
}

/// <summary>Output of one strip query: the server's clock and one snapshot per window.</summary>
public sealed record WindowPoll(long ServerEpoch, IReadOnlyList<WindowSnapshot> Windows);

public static class WindowStripParser
{
    public const string Format = "#{window_id}\t#{window_index}\t#{window_active}\t#{window_bell_flag}\t#{window_activity}\t#{pane_dead}\t#{pane_dead_status}\t#{pane_current_command}\t#{window_name}";
    public const string NowMarker = "@@NOW ";
    public const string PreviewMarker = "@@W ";
    public const int PreviewLines = 2;
    public const int PreviewWidth = 90;

    /// <summary>Shell script for the server: the clock, the windows of one session, then the last lines of each window. Plain sh, so it works whatever the login shell is.</summary>
    public static string Script(string sessionTarget)
    {
        string t = SshCommand.ShellQuote(sessionTarget);
        return $"echo '{NowMarker}'$(date +%s); tmux list-windows -t {t} -F {SshCommand.ShellQuote(Format)}; " +
               $"for w in $(tmux list-windows -t {t} -F '#{{window_id}}'); do echo '{PreviewMarker}'$w; " +
               $"tmux capture-pane -p -t \"$w\" | grep -v '^[[:space:]]*$' | tail -n {PreviewLines} | cut -c1-{PreviewWidth}; done";
    }

    public static WindowPoll Parse(string output)
    {
        long now = 0;
        var records = new List<(string Id, int Index, string Name, string Command, bool Active, bool Bell, long Activity, bool Dead, int? Exit)>();
        var previews = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(NowMarker, StringComparison.Ordinal)) { long.TryParse(line[NowMarker.Length..].Trim(), out now); continue; }
            if (line.StartsWith(PreviewMarker, StringComparison.Ordinal))
            {
                string id = line[PreviewMarker.Length..].Trim();
                current = TmuxWindowParser.IsWindowId(id) ? previews[id] = [] : null;
                continue;
            }
            if (current is not null) { current.Add(Clean(line)); continue; }
            string[] f = line.Split('\t');
            if (f.Length < 9 || !TmuxWindowParser.IsWindowId(f[0]) || !int.TryParse(f[1], out int index) || index < 0) continue;
            long.TryParse(f[4], out long activity);
            int? exit = f[6].Length > 0 && int.TryParse(f[6], out int code) ? code : null;
            records.Add((f[0], index, string.Join('\t', f[8..]), f[7], f[2] == "1", f[3] == "1", activity, f[5] == "1", exit));
        }
        var windows = records.OrderBy(r => r.Index).Select(r => new WindowSnapshot(
            r.Id, r.Index, r.Name, r.Command, r.Active, r.Bell, r.Activity, r.Dead, r.Exit,
            previews.TryGetValue(r.Id, out var p) ? p : [])).ToList();
        return new WindowPoll(now, windows);
    }

    /// <summary>The preview is remote text: no control characters, no escape sequences, tabs as spaces.</summary>
    private static string Clean(string line)
    {
        string text = Regex.Replace(line, "\u001b\\[[0-9;?]*[ -/]*[@-~]", "").Replace('\t', ' ');
        return new string(text.Where(c => !char.IsControl(c)).ToArray()).TrimEnd();
    }
}

/// <summary>
/// Turns polls into cards. The state is an estimate from the output: output within <see cref="RunningSeconds"/> is "running";
/// a window that was running and has been quiet since is "done" until it is looked at; a bell or a question on the last line is "asking".
/// </summary>
public sealed class WindowActivityTracker
{
    public const int RunningSeconds = 10;
    public const int SparkBuckets = 8;
    private static readonly Regex Question = new(
        @"(\?\s*$)|(\[[yY]/[nN]\]\s*:?\s*$)|(\([yY](es)?/[nN](o)?\)\s*:?\s*$)|(yes/no\)?\s*:?\s*$)|(password[^:]*:\s*$)|(press enter)|(continue\?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Memory
    {
        public long LastActivity;
        public DateTimeOffset? RunningSince;
        public DateTimeOffset? DoneAt;
        public DateTimeOffset? AskingSince;
        public readonly List<double> Spark = [];
    }

    private readonly Dictionary<string, Memory> _memory = [];

    public static bool LooksLikeQuestion(string? lastLine) => !string.IsNullOrWhiteSpace(lastLine) && Question.IsMatch(lastLine);

    public IReadOnlyList<WindowCard> Update(WindowPoll poll, DateTimeOffset now, double pollSeconds = 5)
    {
        var live = poll.Windows.Select(w => w.WindowId).ToHashSet();
        foreach (string gone in _memory.Keys.Where(k => !live.Contains(k)).ToList()) _memory.Remove(gone);
        var cards = new List<WindowCard>();
        foreach (var w in poll.Windows)
        {
            if (!_memory.TryGetValue(w.WindowId, out var m)) _memory[w.WindowId] = m = new Memory { LastActivity = w.ActivityEpoch };
            long age = poll.ServerEpoch > 0 && w.ActivityEpoch > 0 ? Math.Max(0, poll.ServerEpoch - w.ActivityEpoch) : long.MaxValue / 4;
            bool changed = w.ActivityEpoch > m.LastActivity;
            m.LastActivity = Math.Max(m.LastActivity, w.ActivityEpoch);
            double bucket = changed ? 0.3 + 0.7 * (1 - Math.Min(age, pollSeconds) / pollSeconds) : age <= RunningSeconds ? 0.25 : 0;
            m.Spark.Add(Math.Clamp(bucket, 0, 1));
            if (m.Spark.Count > SparkBuckets) m.Spark.RemoveAt(0);

            string last = w.Preview.LastOrDefault(l => l.Length > 0) ?? "";
            bool running = !w.Dead && age <= RunningSeconds;
            bool asking = !w.Dead && (w.Bell || (LooksLikeQuestion(last) && age <= 600 && !w.Active));
            if (w.Active && !w.Bell) { m.DoneAt = null; m.AskingSince = null; }   // looked at: nothing left to report
            StripState state;
            if (asking) { m.AskingSince ??= now; m.RunningSince = null; state = StripState.Asking; }
            else if (running) { m.RunningSince ??= now; m.DoneAt = null; m.AskingSince = null; state = StripState.Running; }
            else
            {
                m.AskingSince = null;
                if (w.Dead) { m.DoneAt ??= now; m.RunningSince = null; state = StripState.Done; }
                else
                {
                    if (m.RunningSince is not null) { m.DoneAt = w.Active ? null : now; m.RunningSince = null; }
                    state = m.DoneAt is not null && !w.Active ? StripState.Done : StripState.Idle;
                }
            }
            cards.Add(new WindowCard(w.WindowId, w.Index, w.Name, w.Command, w.Active, state, Label(state), Detail(w, state, m, now, age),
                w.Preview.Where(l => l.Length > 0).TakeLast(WindowStripParser.PreviewLines).ToList(), m.Spark.ToList()));
        }
        return cards;
    }

    private static string Label(StripState state) => state switch
    {
        StripState.Running => "läuft", StripState.Done => "fertig", StripState.Asking => "fragt", _ => "ruhig"
    };

    private static string Detail(WindowSnapshot w, StripState state, Memory m, DateTimeOffset now, long ageSeconds)
    {
        string time = state switch
        {
            StripState.Running => m.RunningSince is { } s ? Duration((long)(now - s).TotalSeconds) : "gerade",
            StripState.Asking => m.AskingSince is { } a ? "wartet " + Duration((long)(now - a).TotalSeconds) : "wartet",
            StripState.Done => (w.Dead && w.ExitStatus is { } code ? $"exit {code} · " : "") + (m.DoneAt is { } d ? "vor " + Ago((long)(now - d).TotalSeconds) : "vor kurzem"),
            _ => ageSeconds >= long.MaxValue / 8 ? "" : Ago(ageSeconds)
        };
        return w.Command.Length > 0 && time.Length > 0 ? w.Command + " · " + time : w.Command + time;
    }

    /// <summary>m:ss for the time something has been running.</summary>
    public static string Duration(long seconds) => seconds < 3600 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds / 3600} h {seconds % 3600 / 60} min";

    /// <summary>German short form for "how long ago".</summary>
    public static string Ago(long seconds) =>
        seconds < 45 ? "gerade" : seconds < 3600 ? $"{Math.Max(1, (seconds + 30) / 60)} min" : seconds < 86400 ? $"{seconds / 3600} h" : $"{seconds / 86400} d";
}
