using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TerminalMgr.Core;
namespace TerminalMgr.App;

public sealed class ProfileItem(Profile profile) : INotifyPropertyChanged
{
    public Profile Profile { get; set; } = profile;
    public string Name => Profile.Name;
    private string _status = profile.AutoConnect ? "Autostart · getrennt" : "Getrennt";
    public string Status { get => _status; set { _status = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { Changed(nameof(Name)); Changed(nameof(Status)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ClipItem : INotifyPropertyChanged
{
    private readonly ClipPreview _preview;
    private bool _expanded;
    public ClipItem(ClipboardEntry entry, bool expanded = false) { Entry = entry; _preview = ClipboardText.Preview(entry.Text); _expanded = expanded && _preview.Truncated; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ClipboardEntry Entry { get; }
    public string FullText => Entry.Text;
    /// <summary>The list shows less than the whole text, so hovering shows a popup and a click can unfold the entry.</summary>
    public bool IsTruncated => _preview.Truncated;
    /// <summary>Unfolded in the list: the full text (about 15 lines, scrollable) instead of the two-line preview.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            value &= IsTruncated;
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new(nameof(CollapsedVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(ExpandedVisibility)));
        }
    }
    public Visibility CollapsedVisibility => _expanded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ExpandedVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Source => Entry.Source;
    public string Time => Entry.Created.ToLocalTime().ToString("HH:mm");
    public string Line1 => _preview.Line1;
    public string? Line2 => _preview.Line2;
    public Visibility Line2Visibility => _preview.Line2 is null ? Visibility.Collapsed : Visibility.Visible;
    public string LengthLabel => _preview.LengthLabel;
    public Visibility LengthVisibility => _preview.LengthLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string PinLabel => Entry.Pinned ? "Lösen" : "Anheften";
}
