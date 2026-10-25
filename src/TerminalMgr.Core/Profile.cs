namespace TerminalMgr.Core;

public enum StartMode { SshConfig, Tmux, Command }

public sealed record Profile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Destination { get; init; } = "";
    public string? ConfigPath { get; init; }
    public bool AutoConnect { get; init; }
    public StartMode Mode { get; init; }
    public string TmuxSession { get; init; } = "main";
    public string Command { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Bitte einen Profilnamen angeben.");
        if (string.IsNullOrWhiteSpace(Destination) || Destination.StartsWith('-') ||
            Destination.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Ziel muss ein SSH-Alias oder user@hostname sein, ohne Leerzeichen oder Optionen.");
        if (Mode == StartMode.Tmux && (string.IsNullOrWhiteSpace(TmuxSession) ||
            TmuxSession.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')))
            throw new ArgumentException("tmux-Name: nur Buchstaben, Zahlen, Unterstrich und Bindestrich.");
        if (Mode == StartMode.Command && string.IsNullOrWhiteSpace(Command))
            throw new ArgumentException("Bitte einen Startbefehl angeben.");
        if (Command.Contains('\0')) throw new ArgumentException("Startbefehl enthält ein ungültiges Zeichen.");
    }
}

public sealed record ClipboardEntry(string Text, string Source, DateTimeOffset Created, bool Pinned = false);
public sealed record AppSettings
{
    public List<Profile> Profiles { get; init; } = [];
    public List<ClipboardEntry> PinnedClips { get; init; } = [];
    public int FontSize { get; init; } = 14;
    public int ClipHistoryLimit { get; init; } = 100;
}
