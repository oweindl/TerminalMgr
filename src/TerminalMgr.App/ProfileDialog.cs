using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;
public sealed class ProfileDialog : Window
{
    private readonly Profile _original;
    private readonly TextBox _name, _destination, _config, _session, _command;
    private readonly CheckBox _automatic, _reconnect;
    private readonly ComboBox _mode;
    public Profile? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public ProfileDialog(Profile? profile)
    {
        _original = profile ?? new Profile();
        Style = (Style)FindResource(typeof(Window));
        Title = profile is null ? "Verbindungsprofil anlegen" : "Verbindungsprofil bearbeiten";
        Width = 560; Height = 730; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var box = new TextBox { Text = value };
            panel.Children.Add(box); return box;
        }
        _name = Field("Anzeigename", _original.Name);
        _destination = Field("SSH-Alias oder user@hostname", _original.Destination);
        _config = Field("SSH-Config (leer = OpenSSH-Standard)", _original.ConfigPath ?? "");
        _automatic = new CheckBox { Content = "Beim Start von TerminalMgr automatisch verbinden", IsChecked = _original.AutoConnect };
        panel.Children.Add(_automatic);
        _reconnect = new CheckBox { Content = "Nach Verbindungsabbruch automatisch neu verbinden", IsChecked = _original.AutoReconnect,
            ToolTip = "Nur bei verlorener Verbindung (SSH-Exit 255), nicht nach exit oder tmux-Detach. Wartezeit 2 bis 30 s, höchstens 10 Versuche." };
        panel.Children.Add(_reconnect);
        panel.Children.Add(new TextBlock { Text = "Nach dem Verbindungsaufbau" });
        _mode = new ComboBox { Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(8),
            ItemsSource = new[] { "SSH-Config verwenden (inklusive RemoteCommand)", "tmux-Session öffnen oder anlegen", "Eigenen Remote-Befehl ausführen" },
            SelectedIndex = (int)_original.Mode };
        panel.Children.Add(_mode);
        _session = Field("tmux-Session", _original.TmuxSession);
        _command = Field("Startbefehl (optional bei tmux)", _original.Command);
        _command.AcceptsReturn = true; _command.Height = 100; _command.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightSlateGray };
        panel.Children.Add(hint);
        void ModeChanged()
        {
            _session.IsEnabled = _mode.SelectedIndex == 1;
            _command.IsEnabled = _mode.SelectedIndex != 0;
            hint.Text = _mode.SelectedIndex switch {
                0 => "OpenSSH wertet deine Config aus. Es wird kein zusätzlicher Startbefehl gesendet.",
                1 => "An bestehende Session anhängen. Der optionale Befehl läuft nur beim Erstellen einer neuen Session.",
                _ => "Der Befehl läuft bei jeder manuellen oder automatischen Verbindung. Nach seinem Ende schließt SSH. Für eine anschließende Shell z. B.: dein-befehl; exec bash -l"
            };
        }
        _mode.SelectionChanged += (_, _) => ModeChanged(); ModeChanged();
        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(buttons);
        var save = new Button { Content = "Speichern", IsDefault = true };
        save.Click += (_, _) => {
            try {
                Result = _original with {
                    Name = _name.Text.Trim(), Destination = _destination.Text.Trim(),
                    ConfigPath = string.IsNullOrWhiteSpace(_config.Text) ? null : _config.Text.Trim(),
                    AutoConnect = _automatic.IsChecked == true, AutoReconnect = _reconnect.IsChecked == true,
                    Mode = (StartMode)_mode.SelectedIndex,
                    TmuxSession = _session.Text.Trim(), Command = _command.Text
                };
                Result.Validate();
                DialogResult = true;
            } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        if (profile is not null)
        {
            var delete = new Button { Content = "Profil löschen" };
            delete.Click += (_, _) => { DeleteRequested = true; DialogResult = true; };
            buttons.Children.Add(delete);
        }
    }
}
