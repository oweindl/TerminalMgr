using System.Text;

namespace TerminalMgr.Core;

public static class SshCommand
{
    public static IReadOnlyList<string> Arguments(Profile profile)
    {
        profile.Validate();
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) args.AddRange(["-F", profile.ConfigPath]);
        args.AddRange(["-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3"]);
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

    private static IReadOnlyList<string> OverrideArguments(Profile profile, bool interactive, string command)
    {
        profile.Validate();
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) args.AddRange(["-F", profile.ConfigPath]);
        args.AddRange([interactive ? "-tt" : "-T", "-o", "RemoteCommand=none", "-o", "ConnectTimeout=8"]);
        if (!interactive) args.AddRange(["-o", "BatchMode=yes"]);
        args.AddRange(["-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3", profile.Destination, command]);
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
