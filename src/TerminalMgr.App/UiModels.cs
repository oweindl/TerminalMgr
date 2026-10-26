using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using TerminalMgr.Core;
namespace TerminalMgr.App;

/// <summary>A session tab: the server, its Alt number, and the tooltip (destination and status).</summary>
public sealed class TabModel(ProfileItem item, string number)
{
    public ProfileItem Item { get; } = item;
    public string Number { get; } = number;
    public string Tooltip => $"{Item.Name} · {Item.Profile.Destination}{Environment.NewLine}{Item.Status}" + (Number.Length > 0 ? $"{Environment.NewLine}Alt+{Number}" : "");
}

/// <summary>One card of the window strip: a tmux window with its state, the last lines and the activity history.</summary>
public sealed class StripCard(WindowCard card)
{
    public WindowCard Card { get; } = card;
    public string WindowId => Card.WindowId;
    public int Index => Card.Index;
    public string Shortcut => Card.Shortcut;
    public string Name => Card.Name;
    public string StateLabel => Card.StateLabel;
    public string Detail => Card.Detail;
    public bool Current => Card.Current;
    public StripState State => Card.State;
    public string Preview1 => Card.Preview.Count > 1 ? Card.Preview[^2] : Card.Preview.Count == 1 ? Card.Preview[0] : "";
    public string Preview2 => Card.Preview.Count > 1 ? Card.Preview[^1] : "";
    public Brush StateBrush => (Brush)Application.Current.FindResource(State switch
    {
        StripState.Running => "AccentBrush", StripState.Done => "ConnectedBrush", StripState.Asking => "RecordingBrush", _ => "LineStrongBrush"
    });
    /// <summary>Heights in pixels (at most 12) of the activity bars, oldest first; always <see cref="WindowActivityTracker.SparkBuckets"/> of them.</summary>
    public IReadOnlyList<double> SparkHeights => Enumerable.Repeat(0.0, Math.Max(0, WindowActivityTracker.SparkBuckets - Card.Spark.Count))
        .Concat(Card.Spark).Select(v => Math.Max(1.5, v * 12)).ToList();
    public string Tooltip => $"{Name} ({(Shortcut.Length > 0 ? Shortcut + " · " : "")}{StateLabel}){Environment.NewLine}{Detail}";
}

public enum ConnectionState { Disconnected, Connecting, Connected }

public sealed class ProfileItem(Profile profile) : INotifyPropertyChanged
{
    public Profile Profile { get; set; } = profile;
    public string Name => Profile.Name;
    private string _status = profile.AutoConnect ? "Autostart · getrennt" : "Getrennt";
    /// <summary>The Alt number of this server in the list ("Alt 1" to "Alt 9"), empty beyond the ninth.</summary>
    private string _altLabel = "";
    public string AltLabel { get => _altLabel; set { if (_altLabel == value) return; _altLabel = value; Changed(); Changed(nameof(AltVisibility)); } }
    public Visibility AltVisibility => _altLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => _status; set { _status = value; Changed(); Changed(nameof(Connection)); } }
    /// <summary>What the tab shows as a dot: green when connected, an amber ring while connecting, nothing otherwise.</summary>
    public ConnectionState Connection => _status.StartsWith('●') ? ConnectionState.Connected
        : _status.StartsWith("Verbinde", StringComparison.Ordinal) || _status.StartsWith("Verbindung verloren", StringComparison.Ordinal) ? ConnectionState.Connecting : ConnectionState.Disconnected;
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
    public Visibility PinnedVisibility => Entry.Pinned ? Visibility.Visible : Visibility.Collapsed;
    public string PinLabel => Entry.Pinned ? "Lösen" : "Anheften";
}
