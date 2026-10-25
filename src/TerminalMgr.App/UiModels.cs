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

public sealed class ClipItem
{
    private readonly ClipPreview _preview;
    public ClipItem(ClipboardEntry entry) { Entry = entry; _preview = ClipboardText.Preview(entry.Text); }
    public ClipboardEntry Entry { get; }
    public string Source => Entry.Source;
    public string Time => Entry.Created.ToLocalTime().ToString("HH:mm");
    public string Line1 => _preview.Line1;
    public string? Line2 => _preview.Line2;
    public Visibility Line2Visibility => _preview.Line2 is null ? Visibility.Collapsed : Visibility.Visible;
    public string LengthLabel => _preview.LengthLabel;
    public Visibility LengthVisibility => _preview.LengthLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string Tooltip => ClipboardText.Excerpt(Entry.Text);
    public string PinLabel => Entry.Pinned ? "Lösen" : "Anheften";
}
