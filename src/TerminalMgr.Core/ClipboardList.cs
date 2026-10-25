namespace TerminalMgr.Core;

/// <summary>Clipboard history, newest first. Texts are unique; copying a known text moves it to the top and makes it active.</summary>
public sealed class ClipboardList
{
    public const int MaxTextLength = 1_000_000;
    private readonly List<ClipboardEntry> _items = [];
    private string? _activeText;

    public IReadOnlyList<ClipboardEntry> Items => _items;
    public int MaxUnpinned { get; set; } = 100;
    public ClipboardEntry? Active => _activeText is null ? null : _items.FirstOrDefault(c => c.Text == _activeText);

    public void LoadPinned(IEnumerable<ClipboardEntry> pinned)
    {
        _items.Clear();
        foreach (var entry in pinned.Where(c => c.Pinned && c.Text.Length > 0 && c.Text.Length <= MaxTextLength)
            .GroupBy(c => c.Text).Select(g => g.First()))
            _items.Add(entry);
        _activeText = _items.FirstOrDefault()?.Text;
    }

    /// <summary>Returns the new or moved entry, or null when the text is empty or too large.</summary>
    public ClipboardEntry? Add(string text, string source, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return null;
        var created = now ?? DateTimeOffset.Now;
        var existing = _items.FirstOrDefault(c => c.Text == text);
        var entry = existing is null ? new ClipboardEntry(text, source, created) : existing with { Created = created };
        if (existing is not null) _items.Remove(existing);
        _items.Insert(0, entry);
        _activeText = text;
        TrimUnpinned();
        return entry;
    }

    public bool Activate(ClipboardEntry entry)
    {
        if (_items.All(c => c.Text != entry.Text)) return false;
        _activeText = entry.Text;
        return true;
    }

    public void TogglePin(ClipboardEntry entry)
    {
        int index = _items.FindIndex(c => c.Text == entry.Text);
        if (index >= 0) _items[index] = _items[index] with { Pinned = !_items[index].Pinned };
        TrimUnpinned();
    }

    public void Remove(ClipboardEntry entry)
    {
        _items.RemoveAll(c => c.Text == entry.Text);
        if (_activeText == entry.Text) _activeText = _items.FirstOrDefault()?.Text;
    }

    public void ClearUnpinned()
    {
        _items.RemoveAll(c => !c.Pinned);
        if (Active is null) _activeText = _items.FirstOrDefault()?.Text;
    }

    private void TrimUnpinned()
    {
        int limit = Math.Max(1, MaxUnpinned);
        while (_items.Count(c => !c.Pinned) > limit)
            _items.RemoveAt(_items.FindLastIndex(c => !c.Pinned));
        if (Active is null) _activeText = _items.FirstOrDefault()?.Text;
    }
}
