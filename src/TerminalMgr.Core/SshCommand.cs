using System.Text;

namespace TerminalMgr.Core;

public static class SshCommand
{
    public static IReadOnlyList<string> Arguments(Profile profile)
    {
        profile.Validate();
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) args.AddRange(["-F", profile.ConfigPath]);
        // A silent network loss (Wi-Fi change, standby, VPN) is noticed after 3 x 15 s; ssh then ends with 255 and the reconnect takes over.
        args.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ConnectTimeout=15"]);
        if (profile.Mode != StartMode.SshConfig)
            args.AddRange(["-tt", "-o", "RemoteCommand=none"]);
        args.Add(profile.Destination);
        if (profile.Mode == StartMode.Tmux)
        {
            var command = "tmux new-session -A -s " + ShellQuote(profile.TmuxSession);
            if (!string.IsNullOrWhiteSpace(profile.Command)) command += " " + ShellQuote(profile.Command);
            args.Add(command);
        }
        else if (profile.Mode == StartMode.Command) args.Add(profile.Command);
        return args;
    }

    public static IReadOnlyList<string> TmuxListArguments(Profile profile) =>
        OverrideArguments(profile, interactive: false, "tmux list-sessions -F " +
            ShellQuote("#{session_id}\t#{session_name}\t#{session_windows}\t#{session_attached}"));

    public static IReadOnlyList<string> TmuxAttachArguments(Profile profile, string sessionId)
    {
        if (!TmuxSessionParser.IsSessionId(sessionId)) throw new ArgumentException("Ungültige tmux-Session-ID.");
        return OverrideArguments(profile, interactive: true, "tmux attach-session -t " + ShellQuote(sessionId));
    }

    /// <summary>All windows of all sessions, one tab-separated line each (see <see cref="TmuxWindowParser.Format"/>); with <paramref name="socket"/> those of another tmux server (<c>tmux -L</c>).</summary>
    public static IReadOnlyList<string> TmuxWindowsArguments(Profile profile, string? socket = null) =>
        OverrideArguments(profile, interactive: false, Tmux(socket) + " list-windows -a -F " + ShellQuote(TmuxWindowParser.Format));

    /// <summary>Windows of one session with the screen of each: one frame, or with <paramref name="loopSeconds"/> a frame per interval until the connection ends (see <see cref="WindowStripParser.Script"/>).</summary>
    public static IReadOnlyList<string> TmuxStripArguments(Profile profile, string sessionTarget, int loopSeconds = 0, string? socket = null)
    {
        if (string.IsNullOrWhiteSpace(sessionTarget) || sessionTarget.Any(char.IsControl)) throw new ArgumentException("Ungültige tmux-Session.");
        return OverrideArguments(profile, interactive: false, "sh -c " + ShellQuote(WindowStripParser.Script(sessionTarget, loopSeconds, socket)));
    }

    /// <summary>Attach straight to one window; an existing session is required, nothing is created.</summary>
    public static IReadOnlyList<string> TmuxAttachArguments(Profile profile, string sessionId, string windowId, string? socket) =>
        OverrideArguments(profile, interactive: true, Tmux(socket) + " attach-session -t " + ShellQuote(TmuxWindowParser.Target(sessionId, windowId)));

    public static IReadOnlyList<string> TmuxAttachArguments(Profile profile, string sessionId, string windowId) => TmuxAttachArguments(profile, sessionId, windowId, null);

    /// <summary>
    /// Creates a tmux session of the normal tmux server, or attaches when the name exists (<c>new-session -A -s</c>); with
    /// <paramref name="window"/> (empty = tmux names it) a new window is opened in it as well. One argument for ssh, no local shell.
    /// </summary>
    public static IReadOnlyList<string> TmuxNewArguments(Profile profile, string session, string? window = null)
    {
        if (!TmuxNaming.IsValid(session)) throw new ArgumentException(TmuxNaming.Rule);
        string command = "tmux new-session -A -s " + ShellQuote(session);
        if (window is not null) command += " ';' " + TmuxNaming.NewWindowCommand(window);   // the name is checked: letters, digits, _ and - need no quoting
        return OverrideArguments(profile, interactive: true, command);
    }

    /// <summary>Closes one tmux window by its stable ID (<c>@n</c>); with <paramref name="socket"/> on another tmux server. One argument for ssh, no local shell.</summary>
    public static IReadOnlyList<string> TmuxKillWindowArguments(Profile profile, string windowId, string? socket = null)
    {
        if (!TmuxWindowParser.IsWindowId(windowId)) throw new ArgumentException("Ungültige tmux-Fenster-ID.");
        return OverrideArguments(profile, interactive: false, Tmux(socket) + " kill-window -t " + ShellQuote(windowId));
    }

    /// <summary>
    /// The snapshot of the tmux sessions, made on the server itself (see <see cref="TmuxSnapshot"/>): one line per window into
    /// <c>~/.config/terminalmgr/tmux-sitzungen.tsv</c>, written atomically with rights for the user only. When no tmux runs (or nothing is left
    /// after leaving out helper sessions) the old file stays. Only the normal tmux server, never a socket of Makyx.
    /// </summary>
    public static IReadOnlyList<string> TmuxSnapshotArguments(Profile profile)
    {
        string format = "#{session_name}\t#{window_index}\t#{window_name}\t#{pane_current_path}";
        string script = "umask 077; d=\"$HOME/.config/terminalmgr\"; f=\"$d/tmux-sitzungen.tsv\"; mkdir -p \"$d\" || exit 0; " +
            "out=$(tmux list-windows -a -F " + ShellQuote(format) + " 2>/dev/null) || exit 0; [ -n \"$out\" ] || exit 0; now=$(date +%s); " +
            "printf '%s\\n' \"$out\" | awk -v t=\"$now\" 'BEGIN{FS=OFS=\"\\t\"} $1 !~ /^(tm|ansicht)-/ {print $0, t}' > \"$f.neu\" " +
            "&& [ -s \"$f.neu\" ] && chmod 600 \"$f.neu\" && mv \"$f.neu\" \"$f\"; rm -f \"$f.neu\"; exit 0";
        return OverrideArguments(profile, interactive: false, "sh -c " + ShellQuote(script));
    }

    /// <summary>One answer with both things the restore question needs: the saved file (if any), then the marker line, then the names of the running sessions.</summary>
    public static IReadOnlyList<string> TmuxMemoryCheckArguments(Profile profile)
    {
        string script = "f=\"$HOME/.config/terminalmgr/tmux-sitzungen.tsv\"; [ -r \"$f\" ] && head -c 262144 \"$f\"; echo; echo '" + TmuxSnapshot.SessionsMarker +
            "'; tmux list-sessions -F '#{session_name}' 2>/dev/null; exit 0";
        return OverrideArguments(profile, interactive: false, "sh -c " + ShellQuote(script));
    }

    /// <summary>
    /// Creates the saved sessions that are missing, window by window in the saved order, each in its directory (the home directory when the
    /// directory is gone): <c>new-session -d</c> for the first window, <c>new-window</c> for the others. A running session is skipped. Only the
    /// session, window names and directories come back, not the programs; every window starts a shell.
    /// </summary>
    public static IReadOnlyList<string> TmuxRestoreArguments(Profile profile, IReadOnlyList<SavedTmuxSession> sessions)
    {
        var todo = sessions.Where(s => s.Windows.Count > 0).ToList();
        if (todo.Count == 0) throw new ArgumentException("Es gibt nichts wiederherzustellen.");
        if (todo.Sum(s => s.Windows.Count) > TmuxSnapshot.MaxWindowsToRestore) throw new ArgumentException("Zu viele Fenster zum Wiederherstellen.");
        string script = "";
        foreach (var session in todo)
        {
            string name = ShellQuote(session.Name), exact = ShellQuote("=" + session.Name), target = ShellQuote("=" + session.Name + ":");
            string inner = "";
            for (int i = 0; i < session.Windows.Count; i++)
            {
                var window = session.Windows[i];
                string label = window.Name.Length > 0 ? " -n " + ShellQuote(window.Name) : "";
                string create = i == 0 ? $"tmux new-session -d -s {name}{label} -c \"$p\"" : $"tmux new-window -t {target}{label} -c \"$p\"";
                inner += $"p={ShellQuote(window.Path)}; [ -d \"$p\" ] || p=\"$HOME\"; {create}; ";
            }
            script += $"tmux has-session -t {exact} 2>/dev/null || {{ {inner}}}; ";
        }
        return OverrideArguments(profile, interactive: false, "sh -c " + ShellQuote(script + "exit 0"));
    }

    /// <summary>"tmux", or "tmux -L 'name'" for a tmux server with its own socket (Makyx runs its instances on one).</summary>
    public static string Tmux(string? socket)
    {
        if (socket is null) return "tmux";
        if (!MakyxName.IsMatch(socket)) throw new ArgumentException("Ungültiger tmux-Socket.");
        return "tmux -L " + ShellQuote(socket);
    }

    private static readonly System.Text.RegularExpressions.Regex MakyxName = new("^[A-Za-z0-9._-]{1,40}$");

    /// <summary>Attach to the tmux window of a Makyx instance: own socket (<c>tmux -L</c>), session and window name as the Makyx dashboard API reports them.</summary>
    public static IReadOnlyList<string> MakyxTmuxAttachArguments(Profile profile, string socket, string session, string window)
    {
        if (!MakyxName.IsMatch(socket) || !MakyxName.IsMatch(session) || !MakyxName.IsMatch(window)) throw new ArgumentException("Ungültiger tmux-Name von Makyx.");
        return OverrideArguments(profile, interactive: true, $"tmux -L {ShellQuote(socket)} attach-session -t {ShellQuote(session + ":" + window)}");
    }

    /// <summary>
    /// Ends the Makyx instance in a tmux window: first "/exit" and Enter (the way Makyx books as finished and frees the slot), then, after a
    /// short wait, the window is closed if it still exists. One argument for ssh, no local shell; names are checked like the attach command.
    /// </summary>
    public static IReadOnlyList<string> MakyxTmuxEndArguments(Profile profile, string socket, string session, string window)
    {
        if (!MakyxName.IsMatch(socket) || !MakyxName.IsMatch(session) || !MakyxName.IsMatch(window)) throw new ArgumentException("Ungültiger tmux-Name von Makyx.");
        string tmux = "tmux -L " + ShellQuote(socket), target = ShellQuote(session + ":" + window);
        string script = $"{tmux} send-keys -t {target} -l '/exit' && {tmux} send-keys -t {target} Enter; sleep 3; {tmux} kill-window -t {target} 2>/dev/null; exit 0";
        return OverrideArguments(profile, interactive: false, "sh -c " + ShellQuote(script));
    }

    /// <summary>Prints the effective OpenSSH settings (ssh -G), e.g. the RemoteCommand from the config.</summary>
    public static IReadOnlyList<string> EffectiveConfigArguments(Profile profile)
    {
        profile.Validate();
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) args.AddRange(["-F", profile.ConfigPath]);
        args.AddRange(["-G", profile.Destination]);
        return args;
    }

    private static IReadOnlyList<string> OverrideArguments(Profile profile, bool interactive, string command)
    {
        profile.Validate();
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) args.AddRange(["-F", profile.ConfigPath]);
        args.AddRange([interactive ? "-tt" : "-T", "-o", "RemoteCommand=none", "-o", "ConnectTimeout=8"]);
        if (!interactive) args.AddRange(["-o", "BatchMode=yes"]);
        args.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", profile.Destination, command]);
        return args;
    }
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    // Windows CreateProcess command-line quoting (not cmd.exe or PowerShell escaping).
    public static string WindowsQuote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string CommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(WindowsQuote));
}
