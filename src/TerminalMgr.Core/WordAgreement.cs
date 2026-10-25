namespace TerminalMgr.Core;

/// <summary>A recognized word with its position in the audio (absolute, from the start of the recording).</summary>
public sealed record TimedWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// Turns the repeated, partly different results for a sliding audio window into a stable running text.
/// A word counts as confirmed when two consecutive results agree on it (local agreement); the words behind it stay tentative
/// and may still change. Confirmed words are never recomputed, and their end marks how far the window can move on.
/// </summary>
public sealed class WordAgreement
{
    private readonly List<TimedWord> _confirmed = [];
    private List<TimedWord> _previous = [];
    private List<TimedWord> _tentative = [];

    public IReadOnlyList<TimedWord> Confirmed => _confirmed;
    public IReadOnlyList<TimedWord> Tentative => _tentative;
    public TimeSpan ConfirmedEnd => _confirmed.Count == 0 ? TimeSpan.Zero : _confirmed[^1].End;
    public string ConfirmedText => Join(_confirmed);
    public string TentativeText => Join(_tentative);
    public string FullText => (ConfirmedText + " " + TentativeText).Trim();

    /// <summary>Takes the result for the current window (word times already absolute) and returns the words that became confirmed by it.</summary>
    public IReadOnlyList<TimedWord> Feed(IReadOnlyList<TimedWord> hypothesis)
    {
        var current = DropConfirmed(hypothesis);
        int agree = 0;
        while (agree < _previous.Count && agree < current.Count && Same(_previous[agree], current[agree])) agree++;
        var newlyConfirmed = current.Take(agree).ToList();
        _confirmed.AddRange(newlyConfirmed);
        _previous = current.Skip(agree).ToList();
        _tentative = _previous;
        return newlyConfirmed;
    }

    public void Reset() { _confirmed.Clear(); _previous = []; _tentative = []; }

    /// <summary>Words that overlap the confirmed part (the window starts a little before the last confirmed word) are not new.</summary>
    private List<TimedWord> DropConfirmed(IReadOnlyList<TimedWord> hypothesis)
    {
        var words = hypothesis.Where(w => Normalize(w.Text).Length > 0 && w.Start >= ConfirmedEnd - TimeSpan.FromMilliseconds(120)).ToList();
        // A word cut by the window edge can come back as a repeat of the last confirmed word at a slightly shifted time.
        for (int length = Math.Min(Math.Min(_confirmed.Count, words.Count), 4); length > 0; length--)
        {
            bool repeated = true;
            for (int i = 0; i < length && repeated; i++)
                repeated = Same(_confirmed[_confirmed.Count - length + i], words[i]);
            if (repeated) { words.RemoveRange(0, length); break; }
        }
        return words;
    }

    private static bool Same(TimedWord a, TimedWord b) => Normalize(a.Text) == Normalize(b.Text);

    public static string Normalize(string word) => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Join(IEnumerable<TimedWord> words) => string.Join(' ', words.Select(w => w.Text.Trim())).Trim();
}

/// <summary>Word error rate (edit distance over words, as a fraction of the reference length) for the comparison of engines.</summary>
public static class WordErrorRate
{
    public static double Compute(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 0 : 1;
        var row = Enumerable.Range(0, h.Length + 1).ToArray();
        for (int i = 1; i <= r.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= h.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (r[i - 1] == h[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return (double)row[h.Length] / r.Length;
    }

    private static string[] Words(string text) =>
        text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(WordAgreement.Normalize).Where(w => w.Length > 0).ToArray();
}
