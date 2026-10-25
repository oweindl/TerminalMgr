using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

public sealed record TmuxWindowRecord(string SessionId, string SessionName, string WindowId, int Index, string Name, string Command, bool Active)
{
    /// <summary>"Index: Name", plus the running program when it differs from the window name.</summary>
    public string Label => Command.Length > 0 && !string.Equals(Command, Name, StringComparison.Ordinal)
        ? $"{Index}: {Name} ({Command})" : $"{Index}: {Name}";
}

public sealed record TmuxSessionWindows(string Id, string Name, IReadOnlyList<TmuxWindowRecord> Windows);

public static partial class TmuxWindowParser
{
    /// <summary>Fixed fields first; the free-text fields last so that tabs inside a name cannot shift the IDs.</summary>
    public const string Format = "#{session_id}\t#{window_id}\t#{window_index}\t#{window_active}\t#{session_name}\t#{window_name}\t#{pane_current_command}";
    public const string Prefix = "\x02"; // Ctrl+B, the tmux default; README documents the assumption.
    public const int MaxFlatEntries = 25;

    public static bool IsWindowId(string value) =>
        value.Length > 1 && value[0] == '@' && value[1..].All(char.IsAsciiDigit);

    public static IReadOnlyList<TmuxWindowRecord> Parse(string output)
    {
        var windows = new List<TmuxWindowRecord>();
        foreach (string line in output.Split('\n'))
        {
            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 7 || !TmuxSessionParser.IsSessionId(f[0]) || !IsWindowId(f[1]) ||
                !int.TryParse(f[2], out int index) || index < 0 || (f[3] != "0" && f[3] != "1")) continue;
            // Session name is the first free-text field, the command the last; anything in between belongs to the window name.
            windows.Add(new(f[0], f[4], f[1], index, string.Join('\t', f[5..^1]), f[^1], f[3] == "1"));
        }
        return windows;
    }

    public static IReadOnlyList<TmuxSessionWindows> Group(IEnumerable<TmuxWindowRecord> windows) =>
        windows.GroupBy(w => w.SessionId)
            .Select(g => new TmuxSessionWindows(g.Key, g.First().SessionName, g.OrderBy(w => w.Index).ToList()))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>More than about 25 entries are shown as one submenu per session instead of a flat list.</summary>
    public static bool UseSubmenus(IReadOnlyList<TmuxSessionWindows> sessions) =>
        sessions.Sum(s => s.Windows.Count + 1) > MaxFlatEntries;

    /// <summary>Pause between the steps of <see cref="SwitchSteps"/>. tmux opens its command prompt only after it has
    /// processed the ":"; anything sent in the same burst would still go to the program in the pane.</summary>
    public static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Keystrokes for the own tmux client, in two steps: prefix and ":", then switch-client to stable IDs and Enter.</summary>
    public static IReadOnlyList<string> SwitchSteps(string sessionId, string windowId) =>
        [Prefix + ":", "switch-client -t " + SshCommand.ShellQuote(Target(sessionId, windowId)) + "\r"];

    public static string Target(string sessionId, string windowId)
    {
        if (!TmuxSessionParser.IsSessionId(sessionId)) throw new ArgumentException("Ungültige tmux-Session-ID.");
        if (!IsWindowId(windowId)) throw new ArgumentException("Ungültige tmux-Fenster-ID.");
        return sessionId + ":" + windowId;
    }

    /// <summary>Session name from a RemoteCommand such as "tmux new -A -s main" or "tmux attach -t main".</summary>
    public static string? SessionNameFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = SessionOption().Match(command);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"\btmux\s+(?:new-session|new|attach-session|attach|at|a)\b[^;&|]*?\s-[A-Za-z]*[st]\s+(?<q>['""]?)(?<name>[^\s'""]+)\k<q>")]
    private static partial Regex SessionOption();
}
