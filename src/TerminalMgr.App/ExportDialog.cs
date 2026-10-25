using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Export of the configuration to a .tmconfig file: what to include, and an optional password.</summary>
public sealed class ExportDialog : Window
{
    private readonly CheckBox _profiles, _settings, _pinned, _hosts;
    private readonly PasswordBox _password = new() { Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 8) };
    private readonly PasswordBox _repeat = new() { Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _passwordHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
    public ExportSelection? Selection { get; private set; }
    public string? Password { get; private set; }
    public IReadOnlyList<SshHostBlock> Hosts { get; }

    public ExportDialog(AppSettings settings, IReadOnlyList<SshHostBlock> hostBlocks)
    {
        Hosts = hostBlocks;
        Style = (Style)FindResource(typeof(Window));
        Title = "Konfiguration exportieren";
        Width = 600; SizeToContent = SizeToContent.Height; MaxHeight = 800; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Was soll in die Datei?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

        _profiles = Check($"Profile mit allen Optionen ({settings.Profiles.Count})", settings.Profiles.Count > 0, settings.Profiles.Count > 0, "Auto-Verbindung, Startmodus, Startaktion, Wiederverbindung");
        _settings = Check("App-Einstellungen", true, true, "Schriftgröße, Tastenkürzel, Diktat ohne Modell und Mikrofon, Update-Einstellungen, Clipboard-Limit");
        int pinnedCount = settings.PinnedClips.Count(c => c.Pinned);
        _pinned = Check($"Angeheftete Clipboard-Einträge ({pinnedCount})", false, pinnedCount > 0, "Vorsicht: angeheftete Texte können Geheimnisse enthalten (Vorgabe: aus).");
        var aliases = hostBlocks.SelectMany(b => b.Aliases).Where(SshConfigBlocks.IsExplicitAlias).Distinct().ToList();
        _hosts = Check($"Host-Blöcke der SSH-Config, die die Profile benutzen ({hostBlocks.Count})", hostBlocks.Count > 0, hostBlocks.Count > 0,
            aliases.Count > 0 ? "Hosts: " + string.Join(", ", aliases) : "Für die Profile wurden keine Host-Blöcke gefunden.");

        var keys = SshConfigBlocks.IdentityFiles(hostBlocks);
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Goldenrod, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            Text = "Nie in der Datei: private Schlüssel, known_hosts, Passwörter und das Whisper-Modell." +
                   (keys.Count > 0 ? "\nDie Host-Blöcke verweisen auf Schlüsseldateien, die Sie separat auf den anderen Rechner kopieren müssen: " + string.Join(", ", keys) : "") });

        panel.Children.Add(new TextBlock { Text = "Passwortschutz (optional)", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "Passwort" });
        panel.Children.Add(_password);
        panel.Children.Add(new TextBlock { Text = "Passwort wiederholen" });
        panel.Children.Add(_repeat);
        panel.Children.Add(_passwordHint);
        void UpdateHint() => _passwordHint.Text = _password.Password.Length == 0
            ? "Ohne Passwort enthält die Datei Hostnamen, Benutzernamen und Befehle im Klartext. Verschlüsselt wird mit AES-256-GCM, der Schlüssel entsteht per PBKDF2 aus dem Passwort."
            : "Die Datei wird verschlüsselt. Ohne das Passwort lässt sie sich nicht importieren; es kann nicht wiederhergestellt werden.";
        _password.PasswordChanged += (_, _) => UpdateHint();
        UpdateHint();

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var export = new Button { Content = "Exportieren …", IsDefault = true };
        export.Click += (_, _) => Accept();
        buttons.Children.Add(export);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);

        CheckBox Check(string label, bool isChecked, bool enabled, string detail)
        {
            var box = new CheckBox { Content = label, IsChecked = isChecked && enabled, IsEnabled = enabled, Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(22, 0, 0, 0) });
            return box;
        }
    }

    private void Accept()
    {
        var selection = new ExportSelection(_profiles.IsChecked == true, _settings.IsChecked == true, _pinned.IsChecked == true, _hosts.IsChecked == true);
        if (!selection.Profiles && !selection.Settings && !selection.PinnedClips && !selection.SshHosts)
        { MessageBox.Show(this, "Bitte mindestens einen Teil auswählen.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_password.Password != _repeat.Password)
        { MessageBox.Show(this, "Die beiden Passwörter stimmen nicht überein.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Selection = selection;
        Password = _password.Password.Length == 0 ? null : _password.Password;
        DialogResult = true;
    }
}
