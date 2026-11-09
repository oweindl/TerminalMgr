namespace TerminalMgr.Core;

/// <summary>One line of the window switcher (note 16, mockup C): a tmux window under its group, with the socket it lives on.</summary>
/// <param name="Group">Heading of the group: the session, or "Makyx · Projekt" for windows of Makyx.</param>
/// <param name="Socket">The tmux socket (Makyx), null for the normal tmux server.</param>
/// <param name="Status">Status of the Makyx instance in this window ("wartet", "arbeitet"), empty otherwise.</param>
/// <param name="Here">The window this tab shows.</param>
public sealed record TmuxSwitcherEntry(string Group, string? Socket, TmuxWindowRecord Window, string Title, string Status, bool Here)
{
    public string SearchText => Group + " " + Window.Name + " " + Title + " " + Window.Command;
}

public static class TmuxSwitcher
{
    /// <summary>Entries whose group, window name, title or program contain every word of <paramref name="query"/> (case ignored).</summary>
    public static IReadOnlyList<TmuxSwitcherEntry> Filter(IReadOnlyList<TmuxSwitcherEntry> entries, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return entries;
        return entries.Where(e => words.All(w => e.SearchText.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    /// <summary>
    /// The entries for Makyx windows: the group is "Makyx · Projekt" when a run of Makyx sits in the window (by tmux window name or index),
    /// title and status come from that run.
    /// </summary>
    public static IReadOnlyList<TmuxSwitcherEntry> MakyxEntries(string socket, IReadOnlyList<TmuxSessionWindows> sessions, IReadOnlyList<MakyxRun> runs, Func<TmuxWindowRecord, bool> here)
    {
        var list = new List<TmuxSwitcherEntry>();
        foreach (var session in sessions)
        {
            string bare = session.Name.StartsWith(MakyxTmux.GroupPrefix, StringComparison.Ordinal) ? session.Name[MakyxTmux.GroupPrefix.Length..] : session.Name;
            foreach (var window in session.Windows)
            {
                var run = RunFor(window, bare, socket, runs);
                string group = MakyxTmux.GroupPrefix + (run is not null ? MakyxNames.ProjectLabel(run) : bare);
                list.Add(new(group, socket, window, run?.Title ?? "", run is null ? "" : MakyxJson.StatusText(run.Status), here(window)));
            }
        }
        return list.OrderBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Window.Index).ToList();
    }

    /// <summary>The run of Makyx that sits in this window: same socket and session, and tmux window name, index or id.</summary>
    public static MakyxRun? RunFor(TmuxWindowRecord window, string session, string socket, IReadOnlyList<MakyxRun> runs) =>
        runs.Where(r => r.IsActive && r.Terminal is { Available: true } t && t.TmuxSocket == socket && (t.TmuxSession is null || t.TmuxSession == session))
            .FirstOrDefault(r => r.Terminal.TmuxWindow is { } w && (w == window.Name || w == window.WindowId || w == window.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static IReadOnlyList<TmuxSwitcherEntry> PlainEntries(IReadOnlyList<TmuxSessionWindows> sessions, Func<TmuxWindowRecord, bool> here) =>
        sessions.SelectMany(s => s.Windows.Select(w => new TmuxSwitcherEntry(s.Name, null, w, "", "", here(w)))).ToList();
}
