using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// Preview of an import: which profiles are new or clash by name (each clash: skip, replace or keep as copy), what else the file brings,
/// and which SSH host blocks would be appended (existing hosts are never overwritten). Nothing is changed before "Importieren".
/// </summary>
public sealed class ImportDialog : Window
{
    private readonly AppSettings _current;
    private readonly ConfigContent _content;
    private readonly IReadOnlyCollection<string> _existingHosts;
    private readonly StackPanel _body = new();
    private readonly RadioButton _merge = new() { Content = "Zusammenführen (vorhandene Daten bleiben, Neues kommt dazu)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _replaceAll = new() { Content = "Alles ersetzen (die Teile der Datei ersetzen die vorhandenen)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<ProfileImportItem, ComboBox> _choices = [];
    private CheckBox _takeProfiles = new(), _takeSettings = new(), _takePinned = new(), _takeHosts = new();
    private ImportPlan _plan;

    public ImportMode Mode { get; private set; }
    public ImportPlan Plan => _plan;
    public Dictionary<string, ConflictChoice> Choices { get; } = [];
    public bool TakeProfiles { get; private set; }
    public bool TakeSettings { get; private set; }
    public bool TakePinned { get; private set; }
    public bool TakeHosts { get; private set; }

    public ImportDialog(AppSettings current, ConfigContent content, ConfigFileInfo info, IReadOnlyCollection<string> existingHostAliases)
    {
        _current = current; _content = content; _existingHosts = existingHostAliases;
        _plan = ConfigImporter.BuildPlan(current, content, existingHostAliases, ImportMode.Merge);
        Style = (Style)FindResource(typeof(Window));
        Title = "Konfiguration importieren";
        Width = 680; Height = 760; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var import = new Button { Content = "Importieren", IsDefault = true };
        import.Click += (_, _) => Accept();
        buttons.Children.Add(import);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        root.Children.Add(buttons);
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = $"Konfiguration vom {info.Exported.ToLocalTime():dd.MM.yyyy HH:mm} (TerminalMgr {info.AppVersion}, Format {info.FormatVersion})", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "Es wird erst etwas geändert, wenn Sie „Importieren“ drücken. Vorher wird settings.json gesichert (backup-Ordner im Datenordner).", Foreground = Brushes.LightSlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        header.Children.Add(_merge);
        header.Children.Add(_replaceAll);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) });
        _merge.Checked += (_, _) => Rebuild();
        _replaceAll.Checked += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        bool replaceAll = _replaceAll.IsChecked == true;
        _plan = ConfigImporter.BuildPlan(_current, _content, _existingHosts, replaceAll ? ImportMode.ReplaceAll : ImportMode.Merge);
        _body.Children.Clear();
        _choices.Clear();
        _takeProfiles = _takeSettings = _takePinned = _takeHosts = new CheckBox();

        if (_content.Profiles is not null)
        {
            int conflicts = _plan.Profiles.Count(p => p.Conflict);
            _takeProfiles = Section($"Profile ({_plan.Profiles.Count}: {_plan.Profiles.Count - conflicts} neu, {conflicts} gleichnamig vorhanden)", _plan.Profiles.Count > 0);
            foreach (var item in _plan.Profiles)
            {
                var row = new DockPanel { Margin = new Thickness(22, 2, 0, 0) };
                string text = $"{item.Incoming.Name} → {item.Incoming.Destination}";
                if (replaceAll) text += item.Conflict ? "   (ersetzt das gleichnamige Profil)" : "";
                else if (!item.Conflict) text += "   (neu)";
                if (!replaceAll && item.Conflict)
                {
                    var choice = new ComboBox { ItemsSource = new[] { "Überspringen", "Ersetzen", "Als Kopie anlegen" }, SelectedIndex = 0, Width = 150, Margin = new Thickness(8, 0, 0, 0) };
                    DockPanel.SetDock(choice, Dock.Right);
                    row.Children.Add(choice);
                    _choices[item] = choice;
                    text += "   (gleichnamig vorhanden)";
                }
                row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                _body.Children.Add(row);
            }
            if (replaceAll) _body.Children.Add(Note($"Alle {_current.Profiles.Count} vorhandenen Profile werden durch die der Datei ersetzt (gleichnamige behalten ihre Kennung)."));
        }
        if (_content.Settings is not null)
        {
            _takeSettings = Section("App-Einstellungen", true);
            _body.Children.Add(Note("Schriftgröße, Tastenkürzel, Diktat (ohne Modell und Mikrofon), Update-Einstellungen, Clipboard-Limit. Modell, Mikrofon und Start mit Windows bleiben, wie sie auf diesem Rechner sind."));
        }
        if (_content.PinnedClips is not null)
        {
            _takePinned = Section($"Angeheftete Clipboard-Einträge ({_plan.NewPinned.Count} neu, {_plan.KnownPinned.Count} schon vorhanden)", _content.PinnedClips.Count > 0);
            if (replaceAll) _body.Children.Add(Note("Die vorhandenen angehefteten Einträge werden ersetzt."));
        }
        if (_content.SshHosts is { Count: > 0 })
        {
            int conflicts = _plan.Hosts.Count(h => h.Conflict);
            _takeHosts = Section($"SSH-Host-Blöcke in ~/.ssh/config übernehmen ({_plan.Hosts.Count - conflicts} neu, {conflicts} schon vorhanden)", _plan.Hosts.Count > conflicts);
            foreach (var host in _plan.Hosts)
                _body.Children.Add(new TextBlock { Margin = new Thickness(22, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Foreground = host.Conflict ? Brushes.Goldenrod : Brushes.White,
                    Text = string.Join(" ", host.Block.Aliases) + (host.Conflict ? $"   Konflikt: {string.Join(", ", host.ExistingAliases)} ist schon definiert und wird nicht überschrieben" : "   (neu)") });
            _body.Children.Add(Note("Vor dem Schreiben wird eine Sicherung config.bak-<Datum> angelegt; vorhandene Hosts bleiben unverändert."));
        }
        if (_body.Children.Count == 0) _body.Children.Add(Note("Die Datei enthält keine importierbaren Teile."));
    }

    private CheckBox Section(string label, bool enabled)
    {
        var box = new CheckBox { Content = label, IsChecked = enabled, IsEnabled = enabled, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        _body.Children.Add(box);
        return box;
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(22, 4, 0, 0) };

    private void Accept()
    {
        Mode = _plan.Mode;
        foreach (var (item, combo) in _choices) Choices[item.Incoming.Id] = (ConflictChoice)Math.Max(0, combo.SelectedIndex);
        TakeProfiles = _takeProfiles.IsChecked == true;
        TakeSettings = _takeSettings.IsChecked == true;
        TakePinned = _takePinned.IsChecked == true;
        TakeHosts = _takeHosts.IsChecked == true;
        if (!TakeProfiles && !TakeSettings && !TakePinned && !TakeHosts)
        { MessageBox.Show(this, "Es ist nichts zum Importieren ausgewählt.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
