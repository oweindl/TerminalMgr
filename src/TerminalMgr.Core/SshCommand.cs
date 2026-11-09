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
