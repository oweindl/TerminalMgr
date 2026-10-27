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

    /// <summary>Shortest run of the initial prompt, in words, that counts as Whisper writing the prompt instead of what was said.</summary>
    public const int MinEchoWords = 4;
    /// <summary>The same word this often in a row is a decoding loop ("Linux Linux Linux …"), not speech.</summary>
    public const int MinLoopWords = 4;

    /// <summary>
    /// Whisper sometimes answers with its initial prompt (the list of technical terms) or with one word repeated over and over,
    /// typically when the window holds little or no speech. Runs of the prompt in its own order and decoding loops are removed.
    /// </summary>
    public static string RemoveArtifacts(string text, string prompt)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var drop = ArtifactMask(words, prompt);
        return drop.Any(d => d) ? string.Join(' ', words.Where((_, i) => !drop[i])) : text;
    }

    public static IReadOnlyList<TimedWord> RemoveArtifacts(IReadOnlyList<TimedWord> words, string prompt)
    {
        var drop = ArtifactMask(words.Select(w => w.Text).ToArray(), prompt);
        return drop.Any(d => d) ? words.Where((_, i) => !drop[i]).ToList() : words;
    }

    private static bool[] ArtifactMask(string[] words, string prompt)
    {
        var drop = new bool[words.Length];
        var normalized = words.Select(WordAgreement.Normalize).ToArray();
        var tokens = (prompt ?? "").Split([' ', ',', ';', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(WordAgreement.Normalize).Where(t => t.Length > 0).ToArray();
        if (tokens.Length >= MinEchoWords)
            for (int i = 0; i < normalized.Length; i++)
            {
                int best = 0;
                for (int j = 0; j < tokens.Length; j++)
                {
                    int k = 0;
                    while (i + k < normalized.Length && j + k < tokens.Length && normalized[i + k].Length > 0 && normalized[i + k] == tokens[j + k]) k++;
                    best = Math.Max(best, k);
                }
                if (best < MinEchoWords) continue;
                for (int k = 0; k < best; k++) drop[i + k] = true;
                i += best - 1;
            }
        for (int i = 0; i < normalized.Length; i++)
        {
            int run = 1;
            while (i + run < normalized.Length && normalized[i].Length > 0 && normalized[i + run] == normalized[i]) run++;
            if (run >= MinLoopWords) for (int k = 1; k < run; k++) drop[i + k] = true;
            i += run - 1;
        }
        return drop;
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
