using System.ComponentModel;
using System.Runtime.CompilerServices;
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
public sealed record ClipItem(ClipboardEntry Entry)
{
    public string Source => Entry.Source;
    public string Time => Entry.Created.ToLocalTime().ToString("HH:mm");
    public string Preview => Entry.Text.Length > 500 ? Entry.Text[..500] + " …" : Entry.Text;
    public string PinLabel => Entry.Pinned ? "Lösen" : "Anheften";
}
