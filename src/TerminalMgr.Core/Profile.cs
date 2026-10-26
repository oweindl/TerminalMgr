namespace TerminalMgr.Core;

public enum StartMode { SshConfig, Tmux, Command }

public sealed record Profile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Destination { get; init; } = "";
    public string? ConfigPath { get; init; }
    public bool AutoConnect { get; init; }
    /// <summary>Connect again after the connection was lost (off by default).</summary>
    public bool AutoReconnect { get; init; }
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
    /// <summary>Which recognizer dictates, one of <see cref="SpeechEngineIds.All"/>. Only "whisper" works without a Windows privacy setting or a cloud service.</summary>
    public string Engine { get; init; } = SpeechEngineIds.Whisper;
    /// <summary>Small, fast model for the provisional text while speaking; the configured <see cref="Model"/> makes the final text.</summary>
    public string InterimModel { get; init; } = "small-q5_1";
    /// <summary>The provisional text is computed from at most this much of the latest audio.</summary>
    public double InterimWindowSeconds { get; init; } = 7;
    /// <summary>Pause between two provisional results.</summary>
    public double InterimCycleSeconds { get; init; } = 0.7;

    public DictationOptions Normalized() => this with {
        Engine = SpeechEngineIds.Normalize(Engine),
        InterimModel = WhisperModels.Get(InterimModel).Name,
        InterimWindowSeconds = Math.Clamp(InterimWindowSeconds, 4, 12),
        InterimCycleSeconds = Math.Clamp(InterimCycleSeconds, 0.4, 3),
        Shortcut = Core.Shortcut.TryParse(Shortcut, out var s) ? s.ToString() : "Ctrl+Y",
        Model = WhisperModels.Get(Model).Name,
        Language = Language is "en" or "auto" ? Language : "de",
        SilenceSeconds = Math.Clamp(SilenceSeconds, 0.5, 10),
        LevelThreshold = Math.Clamp(LevelThreshold, 0.002, 0.2)
    };
}

/// <summary>App shortcuts besides dictation (which keeps its own setting): terminal copy/paste and the session switch.</summary>
public sealed record ShortcutOptions
{
    public string Copy { get; init; } = "Ctrl+Shift+C";
    public string Paste { get; init; } = "Ctrl+Shift+V";
    /// <summary>Modifier for session switching together with 1–9, one of <see cref="ShortcutRules.SessionModifiers"/>.</summary>
    public string SessionModifier { get; init; } = "Alt";
    /// <summary>Modifier for the tmux window of the current session together with 0–9, one of <see cref="ShortcutRules.WindowModifiers"/>.</summary>
    public string WindowModifier { get; init; } = "Ctrl";
    public string NextWindow { get; init; } = "Ctrl+Tab";
    public string PreviousWindow { get; init; } = "Ctrl+Shift+Tab";
    /// <summary>Back to the window used before, across servers.</summary>
    public string Back { get; init; } = "Ctrl+<";
    /// <summary>Opens the command palette. Not Ctrl+K: that is "kill to end of line" in bash/readline.</summary>
    public string Palette { get; init; } = "Ctrl+Shift+K";

    public ShortcutOptions Normalized() => this with {
        WindowModifier = ShortcutRules.WindowModifiers.Contains(WindowModifier) ? WindowModifier : "Ctrl",
        NextWindow = Shortcut.TryParse(NextWindow, out var next) ? next.ToString() : "Ctrl+Tab",
        PreviousWindow = Shortcut.TryParse(PreviousWindow, out var previous) ? previous.ToString() : "Ctrl+Shift+Tab",
        Back = Shortcut.TryParse(Back, out var back) ? back.ToString() : "Ctrl+<",
        Palette = Shortcut.TryParse(Palette, out var palette) ? palette.ToString() : "Ctrl+Shift+K",
        Copy = Shortcut.TryParse(Copy, out var copy) ? copy.ToString() : "Ctrl+Shift+C",
        Paste = Shortcut.TryParse(Paste, out var paste) ? paste.ToString() : "Ctrl+Shift+V",
        SessionModifier = ShortcutRules.SessionModifiers.Contains(SessionModifier) ? SessionModifier : "Alt"
    };
}

public sealed record UpdateOptions
{
    /// <summary>Look for a newer release when the app starts (on by default; fails quietly without network).</summary>
    public bool CheckOnStart { get; init; } = true;
    /// <summary>"Skip this version": a release with exactly this version is not offered again.</summary>
    public string? SkippedVersion { get; init; }
}

public sealed record AppSettings
{
    public UpdateOptions Update { get; init; } = new();
    public DictationOptions Dictation { get; init; } = new();
    public ShortcutOptions Shortcuts { get; init; } = new();
    /// <summary>Start TerminalMgr when the user signs in to Windows (off by default).</summary>
    public bool StartWithWindows { get; init; }
    /// <summary>Take clipboard writes that programs request with OSC 52 (tmux forwards copies this way). On by default.</summary>
    public bool AcceptOsc52 { get; init; } = true;
    /// <summary>Copy a mouse selection as soon as it is made, in a plain shell and in tmux or Claude Code alike. Off: it is copied with a right click.</summary>
    public bool CopyOnSelect { get; init; }
    public List<Profile> Profiles { get; init; } = [];
    public List<ClipboardEntry> PinnedClips { get; init; } = [];
    /// <summary>Saved tmux windows with their own shortcut (see <see cref="Favorite"/>).</summary>
    public List<Favorite> Favorites { get; init; } = [];
    public int FontSize { get; init; } = 14;
    public int ClipHistoryLimit { get; init; } = 100;
}
