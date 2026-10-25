namespace TerminalMgr.Core;

/// <summary>The recognizers behind the one dictation interface.</summary>
public static class SpeechEngineIds
{
    /// <summary>whisper.cpp through Whisper.net, local, GPU (Vulkan) when available.</summary>
    public const string Whisper = "whisper";
    /// <summary>Web Speech API inside WebView2.</summary>
    public const string WebSpeech = "webspeech";
    /// <summary>Windows.Media.SpeechRecognition.</summary>
    public const string Windows = "windows";

    public static IReadOnlyList<string> All { get; } = [Whisper, WebSpeech, Windows];

    public static string Normalize(string? id) => All.FirstOrDefault(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase)) ?? Whisper;

    public static string Label(string id) => id switch
    {
        WebSpeech => "Web Speech API (WebView2)",
        Windows => "Windows-Spracherkennung",
        _ => "Whisper (lokal, GPU)"
    };
}

/// <summary>Size rules of the dictation panel in the right column.</summary>
public static class DictationLayout
{
    /// <summary>The panel takes at most a third of the window height (but never less than the buttons and a few lines need).</summary>
    public static double MaxPanelHeight(double windowHeight) => Math.Max(150, windowHeight / 3);
}
