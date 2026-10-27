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
    /// <summary>The same word this often in a row is a decoding loop ("Linux, Linux, Linux, Linux"), not speech ("ja ja ja" is).</summary>
    public const int MinWordRepeats = 4;
    /// <summary>A phrase of two to <see cref="MaxLoopPhrase"/> words this often in a row is a loop as well.</summary>
    public const int MinLoopRepeats = 3;
    /// <summary>Longest phrase (in words) that is checked for loops.</summary>
    public const int MaxLoopPhrase = 4;

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
        MarkLoops(normalized, drop);
        return drop;
    }

    /// <summary>
    /// Marks the repeats of a word (<see cref="MinWordRepeats"/> times in a row) or short phrase (<see cref="MinLoopRepeats"/> times); the first occurrence stays.
    /// Punctuation on its own ("," between two repeats) does not break a run and goes with the repeats it sits between.
    /// </summary>
    private static void MarkLoops(string[] normalized, bool[] drop)
    {
        var content = Enumerable.Range(0, normalized.Length).Where(i => normalized[i].Length > 0).ToArray();
        var dropped = new bool[content.Length];
        for (int n = 1; n <= MaxLoopPhrase; n++)
        {
            int needed = n == 1 ? MinWordRepeats : MinLoopRepeats;
            for (int i = 0; i + n * needed <= content.Length; i++)
            {
                int repeats = 1;
                while (i + (repeats + 1) * n <= content.Length && PhraseEquals(normalized, content, i, i + repeats * n, n)) repeats++;
                if (repeats < needed) continue;
                for (int k = i + n; k < i + repeats * n; k++) dropped[k] = true;
                i += repeats * n - 1;
            }
        }
        for (int c = 0; c < content.Length; c++) if (dropped[c]) drop[content[c]] = true;
        // Punctuation-only entries: they follow the content word before them (dropped with it) or sit between two dropped words.
        for (int i = 0; i < normalized.Length; i++)
        {
            if (normalized[i].Length > 0) continue;
            int before = i - 1; while (before >= 0 && normalized[before].Length == 0) before--;
            int after = i + 1; while (after < normalized.Length && normalized[after].Length == 0) after++;
            if (before >= 0 && drop[before]) drop[i] = true;
        }
    }

    private static bool PhraseEquals(string[] normalized, int[] content, int a, int b, int length)
    {
        for (int k = 0; k < length; k++) if (normalized[content[a + k]] != normalized[content[b + k]]) return false;
        return true;
    }

    /// <summary>The text without decoding loops: "Linux, Linux, Linux, Linux" becomes "Linux,"; a phrase repeated over and over is kept once.</summary>
    public static string CollapseRepeats(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var drop = new bool[words.Length];
        MarkLoops(words.Select(WordAgreement.Normalize).ToArray(), drop);
        return drop.Any(d => d) ? string.Join(' ', words.Where((_, i) => !drop[i])) : text;
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
