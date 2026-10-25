using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

public static partial class DictationText
{
    // Whisper writes non-speech as "[BLANK_AUDIO]", "[Musik]", "(Applaus)", "*Musik*" or with music notes.
    [GeneratedRegex(@"\[[^\]]{0,40}\]|\((?:Musik|Applaus|Stille|Gelächter|music|applause|silence|laughter)[^)]{0,30}\)|\*[A-Za-zÄÖÜäöü ]{1,20}\*|[♪♫]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeech();
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    /// <summary>Recognized text without non-speech markers, control characters and doubled spaces.</summary>
    public static string Clean(string recognized)
    {
        string text = ClipboardText.StripControlCharacters(recognized).Replace('\n', ' ');
        text = NonSpeech().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>Inserts text at the caret of an edited dictation: a space is added only where words would otherwise touch.</summary>
    public static (string Text, int Caret) InsertAtCaret(string existing, int caret, string insert)
    {
        caret = Math.Clamp(caret, 0, existing.Length);
        if (insert.Length == 0) return (existing, caret);
        string before = existing[..caret], after = existing[caret..];
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && !char.IsWhiteSpace(insert[0]) && !".,;:!?)".Contains(insert[0]))
            insert = " " + insert;
        if (after.Length > 0 && !char.IsWhiteSpace(after[0]) && !char.IsWhiteSpace(insert[^1]) && !".,;:!?)".Contains(after[0]))
            insert += " ";
        return (before + insert + after, before.Length + insert.Length);
    }

    /// <summary>
    /// What reaches the terminal: control characters except line break and tab removed, surrounding blank space trimmed
    /// so that a stray trailing line break cannot act as Enter.
    /// </summary>
    public static string ForPaste(string text) => ClipboardText.StripControlCharacters(text).Trim(' ', '\t', '\n');
}
