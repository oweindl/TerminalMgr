namespace TerminalMgr.Core;

/// <summary>One saved tmux window: where it was (session, index), what it was called and its working directory.</summary>
public sealed record SavedTmuxWindow(string Session, int Index, string Name, string Path, long Epoch);

/// <summary>The saved windows of one session in the order they had.</summary>
public sealed record SavedTmuxSession(string Name, IReadOnlyList<SavedTmuxWindow> Windows)
{
    public DateTimeOffset Saved => Windows.Count == 0 ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeSeconds(Windows.Max(w => w.Epoch));
}

/// <summary>What the server answers to the check before connecting: the saved file and the sessions that run now.</summary>
public sealed record TmuxMemoryCheck(IReadOnlyList<SavedTmuxWindow> Saved, IReadOnlyList<string> Running);

/// <summary>
/// The memory of tmux sessions on the server (note 17, order H): a file <c>~/.config/terminalmgr/tmux-sitzungen.tsv</c> with one tab-separated line
/// per window (session, index, name, directory, time). The snapshot is made on the server itself; nothing from the windows is sent around.
/// Only sessions, window names, order and directories are kept, never the programs: restoring starts a shell in each window.
/// This class reads and writes the format and decides what is missing; the shell scripts are in <see cref="SshCommand"/>.
/// </summary>
public static class TmuxSnapshot
{
    public const string RemoteFile = "~/.config/terminalmgr/tmux-sitzungen.tsv";
    public const string SessionsMarker = "@@SESSIONS";
    public const int MaxLines = 2000, MaxWindowsToRestore = 200, MaxName = 100, MaxPath = 1000;

    /// <summary>Helper sessions of TerminalMgr and Makyx views are never part of the memory.</summary>
    public static bool IsHelperSession(string name) => name.StartsWith("tm-", StringComparison.Ordinal) || name.StartsWith("ansicht-", StringComparison.Ordinal);

    /// <summary>
    /// Reads the file. A line that is not exactly five fields (session, index, name, directory, time), has control characters, a bad number or an
    /// over-long text is skipped, so one broken line never loses the rest.
    /// </summary>
    public static IReadOnlyList<SavedTmuxWindow> Parse(string? text)
    {
        var windows = new List<SavedTmuxWindow>();
        if (string.IsNullOrEmpty(text)) return windows;
        int lines = 0;
        foreach (string raw in text.Split('\n'))
        {
            if (++lines > MaxLines) break;
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f.Length != 5) continue;
            if (f[0].Length is 0 or > MaxName || f[2].Length > MaxName || f[3].Length > MaxPath) continue;
            if (!int.TryParse(f[1], out int index) || index < 0 || !long.TryParse(f[4], out long epoch) || epoch < 0) continue;
            if (f.Any(field => field.Any(char.IsControl))) continue;
            if (IsHelperSession(f[0])) continue;
            windows.Add(new SavedTmuxWindow(f[0], index, f[2], f[3], epoch));
        }
        return windows;
    }

    /// <summary>The file as the server writes it (tests and the dialog's own copy); tabs and line breaks in a name become spaces.</summary>
    public static string Format(IEnumerable<SavedTmuxWindow> windows)
    {
        static string Clean(string s) => new(s.Select(c => c is '\t' or '\n' or '\r' ? ' ' : c).ToArray());
        return string.Concat(windows.Select(w => $"{Clean(w.Session)}\t{w.Index}\t{Clean(w.Name)}\t{Clean(w.Path)}\t{w.Epoch}\n"));
    }

    /// <summary>Output of the check script: the file, then <see cref="SessionsMarker"/>, then the names of the running sessions.</summary>
    public static TmuxMemoryCheck ParseCheck(string? output)
    {
        string text = output ?? "";
        int at = text.IndexOf(SessionsMarker, StringComparison.Ordinal);
        string file = at >= 0 ? text[..at] : text;
        var running = at < 0 ? [] : text[(at + SessionsMarker.Length)..].Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        return new TmuxMemoryCheck(Parse(file), running);
    }

    /// <summary>Sessions in the order they first appear in the file, their windows by index.</summary>
    public static IReadOnlyList<SavedTmuxSession> Sessions(IEnumerable<SavedTmuxWindow> windows) =>
        windows.GroupBy(w => w.Session, StringComparer.Ordinal)
            .Select(g => new SavedTmuxSession(g.Key, g.OrderBy(w => w.Index).ToList())).ToList();

    /// <summary>The saved sessions that do not run now (a running session is never touched), at most <see cref="MaxWindowsToRestore"/> windows in all.</summary>
    public static IReadOnlyList<SavedTmuxSession> Missing(IEnumerable<SavedTmuxWindow> saved, IEnumerable<string> running)
    {
        var present = running.ToHashSet(StringComparer.Ordinal);
        var result = new List<SavedTmuxSession>();
        int budget = MaxWindowsToRestore;
        foreach (var session in Sessions(saved))
        {
            if (present.Contains(session.Name) || budget <= 0) continue;
            var windows = session.Windows.Take(budget).ToList();
            budget -= windows.Count;
            result.Add(session with { Windows = windows });
        }
        return result;
    }

    /// <summary>The line in the question: "main (bash, logs), arbeit (claude)".</summary>
    public static string Summary(IEnumerable<SavedTmuxSession> sessions) =>
        string.Join(", ", sessions.Select(s => $"{s.Name} ({string.Join(", ", s.Windows.Select(w => w.Name.Length > 0 ? w.Name : "Fenster " + w.Index))})"));

    /// <summary>What decides whether the server needs a new snapshot: the windows of the session the strip shows, with their IDs, positions and names.</summary>
    public static string Signature(IEnumerable<(string Id, int Index, string Name)> windows) =>
        string.Join("|", windows.Select(w => $"{w.Id}:{w.Index}:{w.Name}"));
}

/// <summary>
/// When a snapshot is written for a server: after a change of the windows, at most once a minute, and as a refresh every ten minutes (the strip
/// only sees one session). Never while it is suspended ("Später fragen": the file must not be overwritten before the question was answered).
/// Pure, so the rules are testable.
/// </summary>
public sealed class TmuxSnapshotPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1), Refresh = TimeSpan.FromMinutes(10);

    private sealed class State { public string? Latest, Written; public DateTimeOffset? LastWrite; public bool Suspended; }
    private readonly Dictionary<string, State> _states = [];
    private State Get(string key) => _states.TryGetValue(key, out var s) ? s : _states[key] = new State();

    /// <summary>What the windows look like now.</summary>
    public void Observe(string key, string signature) => Get(key).Latest = signature;

    /// <summary>The connection is new: its state is unknown, the first snapshot may be written at once (a suspension stays).</summary>
    public void Reset(string key) { var s = Get(key); s.Latest = null; s.Written = null; s.LastWrite = null; }

    public void Suspend(string key) => Get(key).Suspended = true;
    public void Resume(string key) => Get(key).Suspended = false;
    public bool IsSuspended(string key) => Get(key).Suspended;

    /// <summary>A change that was not written yet, or a refresh that is overdue, and the minimum interval has passed.</summary>
    public bool Due(string key, DateTimeOffset now)
    {
        var s = Get(key);
        if (s.Suspended) return false;
        bool changed = (s.Latest ?? "-") != s.Written || (s.LastWrite is { } last && now - last >= Refresh);
        return changed && (s.LastWrite is null || now - s.LastWrite.Value >= MinInterval);
    }

    /// <summary>The write that was started with the state of <see cref="Observe"/> at that moment succeeded.</summary>
    public void Written(string key, string? signature, DateTimeOffset now)
    {
        var s = Get(key);
        s.Written = signature ?? "-";
        s.LastWrite = now;
    }

    /// <summary>The signature to hand to <see cref="Written"/> when a write starts.</summary>
    public string Current(string key) => Get(key).Latest ?? "-";
}
