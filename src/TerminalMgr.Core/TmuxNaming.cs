namespace TerminalMgr.Core;

/// <summary>Names for tmux sessions and windows that TerminalMgr creates itself: the same safe set as the tmux name of a profile.</summary>
public static class TmuxNaming
{
    public const int MaxLength = 40;
    public const string Rule = "tmux-Name: nur Buchstaben, Zahlen, Unterstrich und Bindestrich (höchstens 40 Zeichen).";

    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The message for a refused name, or null when it is fine. An optional name may be empty (tmux then chooses).</summary>
    public static string? Problem(string? name, bool optional)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return optional ? null : "Bitte einen Namen angeben.";
        return IsValid(trimmed) ? null : Rule;
    }

    /// <summary>A valid session name made from a server name ("Mein Server 1" becomes "Mein-Server-1"); "main" when nothing is left.</summary>
    public static string Suggest(string? serverName)
    {
        var chars = (serverName ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray();
        string name = new string(chars).Trim('-');
        if (name.Length > MaxLength) name = name[..MaxLength].Trim('-');
        return name.Length == 0 ? "main" : name;
    }

    /// <summary>The tmux command that opens a new window in the session of the own client; <paramref name="name"/> may be empty.</summary>
    public static string NewWindowCommand(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "new-window";
        if (!IsValid(trimmed)) throw new ArgumentException(Rule);
        return "new-window -n " + trimmed;
    }
}
