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
public sealed record DictationOptions
{
    public string Shortcut { get; init; } = "Ctrl+Y";
    /// <summary>Name of the recording device; null or empty means the Windows default.</summary>
    public string? Microphone { get; init; }
    public string Model { get; init; } = WhisperModels.DefaultName;
    /// <summary>"de", "en" or "auto".</summary>
    public string Language { get; init; } = "de";
    public string InitialPrompt { get; init; } = "tmux, git, Claude Code, Docker, SSH, GitHub, PowerShell, Linux, npm, Kubernetes";
    public double SilenceSeconds { get; init; } = 1.5;
    public double LevelThreshold { get; init; } = 0.015;

    public DictationOptions Normalized() => this with {
        Shortcut = Core.Shortcut.TryParse(Shortcut, out var s) ? s.ToString() : "Ctrl+Y",
        Model = WhisperModels.Get(Model).Name,
        Language = Language is "en" or "auto" ? Language : "de",
        SilenceSeconds = Math.Clamp(SilenceSeconds, 0.5, 10),
        LevelThreshold = Math.Clamp(LevelThreshold, 0.002, 0.2)
    };
}

public sealed record AppSettings
{
    public DictationOptions Dictation { get; init; } = new();
    public List<Profile> Profiles { get; init; } = [];
    public List<ClipboardEntry> PinnedClips { get; init; } = [];
    public int FontSize { get; init; } = 14;
    public int ClipHistoryLimit { get; init; } = 100;
}
