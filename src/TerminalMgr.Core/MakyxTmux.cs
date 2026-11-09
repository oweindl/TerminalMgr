namespace TerminalMgr.Core;

/// <summary>
/// The tmux windows of Makyx instances. Makyx runs them on a tmux server with its own socket (<c>tmux -L makyx</c>), so they do not
/// show up among the normal tmux sessions of a server; TerminalMgr asks that socket separately.
/// </summary>
public static class MakyxTmux
{
    public const string GroupPrefix = "Makyx · ";

    /// <summary>
    /// Sessions and windows as the server menu shows them. The Makyx runner looks at every window through a grouped session of its own
    /// ("ansicht-…"), so each window appears more than once in <c>list-windows -a</c>; each is kept once, under its real session. The
    /// placeholder window that only sleeps is left out.
    /// </summary>
    public static IReadOnlyList<TmuxSessionWindows> Sessions(IEnumerable<TmuxWindowRecord> windows)
    {
        var unique = windows.Where(w => w.Command != "sleep")
            .GroupBy(w => w.WindowId)
            .Select(g => g.OrderBy(w => IsView(w.SessionName) ? 1 : 0).ThenBy(w => w.SessionName, StringComparer.Ordinal).First());
        return TmuxWindowParser.Group(unique).Select(s => s with { Name = GroupPrefix + s.Name }).ToList();
    }

    /// <summary>A grouped viewing session (of the runner, "ansicht-…", or of a TerminalMgr tab, "tm-…"), not a session of its own.</summary>
    public static bool IsView(string session) => session.StartsWith("ansicht-", StringComparison.Ordinal) || IsOwnView(session);

    /// <summary>The viewing session of a TerminalMgr tab: "tm-" and eight hex digits.</summary>
    public static bool IsOwnView(string? session) => session is { Length: 11 } && session.StartsWith("tm-", StringComparison.Ordinal) && session[3..].All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// A new name for the viewing session of one tab. Every Makyx tab looks at the Makyx session through a grouped session of its own, so
    /// switching windows in one tab never moves another (note 16, point 4); tmux removes it when the tab disconnects (destroy-unattached).
    /// </summary>
    public static string NewViewName() => "tm-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The tmux sockets of the Makyx machines that belong to this profile (same rules as "Im Terminal öffnen").</summary>
    public static IReadOnlyList<string> SocketsFor(Profile profile, IReadOnlyList<Profile> profiles, IEnumerable<MakyxMachine> machines, IReadOnlyDictionary<string, string>? map = null) =>
        machines.Where(m => m.TmuxSocket is { Length: > 0 } && MakyxProfileMatcher.Match(profiles, m.Name, m.SshTarget, map)?.Id == profile.Id)
            .Select(m => m.TmuxSocket!).Distinct(StringComparer.Ordinal).ToList();
}
