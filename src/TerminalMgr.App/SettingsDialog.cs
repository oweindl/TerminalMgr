using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Application settings: general, shortcuts, clipboard and dictation.</summary>
public enum ConfigAction { None, Export, Import }

public sealed class SettingsDialog : Window
{
    private const string DefaultMicrophone = "Windows-Standard";
    private readonly AppSettings _original;
    private readonly StackPanel _panel = new() { Margin = new Thickness(24) };
    private readonly CheckBox _startWithWindows, _acceptOsc52, _checkUpdates;
    private string? _skippedVersion;
    /// <summary>The user pressed "Jetzt nach Updates suchen": the settings are saved and the caller runs the check.</summary>
    public bool CheckForUpdatesNow { get; private set; }
    /// <summary>"Exportieren" or "Importieren" was pressed: the settings are saved and the caller opens the matching dialog.</summary>
    public ConfigAction ConfigAction { get; private set; }
    private readonly TextBox _historyLimit, _dictationShortcut, _copyShortcut, _pasteShortcut, _prompt, _silence, _threshold;
    private readonly ComboBox _microphone, _model, _interimModel, _language, _sessionModifier, _engine, _windowModifier;
    private readonly TextBox _nextShortcut, _previousShortcut, _backShortcut, _paletteShortcut;
    private List<Favorite> _favorites;
    public AppSettings? Result { get; private set; }

    public SettingsDialog(AppSettings settings, IReadOnlyList<NavigationPosition>? recentWindows = null)
    {
        _original = settings;
        _favorites = [.. settings.Favorites];
        var dictation = settings.Dictation.Normalized();
        var shortcuts = settings.Shortcuts.Normalized();
        Style = (Style)FindResource(typeof(Window));
        Title = "Einstellungen";
        Width = 620; Height = 860; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Heading("Allgemein");
        _startWithWindows = new CheckBox { Content = "TerminalMgr bei der Windows-Anmeldung starten", IsChecked = settings.StartWithWindows };
        _panel.Children.Add(_startWithWindows);

        Heading("Updates");
        _skippedVersion = settings.Update.SkippedVersion;
        _checkUpdates = new CheckBox { Content = "Beim Start nach neuen Versionen suchen", IsChecked = settings.Update.CheckOnStart,
            ToolTip = "Fragt anonym die öffentliche GitHub-Release-Liste ab; ohne Netz startet die App unverändert weiter." };
        _panel.Children.Add(_checkUpdates);
        var updateButtons = new WrapPanel();
        var checkNow = new Button { Content = "Jetzt nach Updates suchen", ToolTip = "Speichert die Einstellungen und sucht sofort nach einer neueren Version." };
        checkNow.Click += (_, _) => { CheckForUpdatesNow = true; Save(); };
        var resetSkipped = new Button { Content = _skippedVersion is null ? "Keine Version übersprungen" : $"Übersprungene Version {_skippedVersion} zurücksetzen", IsEnabled = _skippedVersion is not null };
        resetSkipped.Click += (_, _) => { _skippedVersion = null; resetSkipped.Content = "Keine Version übersprungen"; resetSkipped.IsEnabled = false; };
        updateButtons.Children.Add(checkNow);
        updateButtons.Children.Add(resetSkipped);
        _panel.Children.Add(updateButtons);

        Heading("Konfiguration");
        _panel.Children.Add(new TextBlock { Text = "Profile, Einstellungen, angeheftete Clipboard-Einträge und SSH-Host-Blöcke in eine .tmconfig-Datei sichern (optional mit Passwort) oder auf einem anderen Rechner einlesen.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
        var configButtons = new WrapPanel();
        var export = new Button { Content = "Konfiguration exportieren …", ToolTip = "Speichert die Einstellungen und öffnet die Auswahl." };
        export.Click += (_, _) => { ConfigAction = ConfigAction.Export; Save(); };
        var import = new Button { Content = "Konfiguration importieren …", ToolTip = "Speichert die Einstellungen, zeigt vor dem Einlesen eine Vorschau und legt Sicherungen an." };
        import.Click += (_, _) => { ConfigAction = ConfigAction.Import; Save(); };
        configButtons.Children.Add(export);
        configButtons.Children.Add(import);
        _panel.Children.Add(configButtons);

        Heading("Tastenkürzel");
        _panel.Children.Add(new TextBlock { Text = "Feld anklicken und Kombination drücken. Mit Ctrl oder Alt, nur F1 bis F12 gehen allein. Ctrl+B bleibt für tmux reserviert.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        _dictationShortcut = ShortcutField("Diktat starten und stoppen", dictation.Shortcut, new DictationOptions().Shortcut);
        _copyShortcut = ShortcutField("Markierung kopieren", shortcuts.Copy, new ShortcutOptions().Copy);
        _pasteShortcut = ShortcutField("Zwischenablage einfügen", shortcuts.Paste, new ShortcutOptions().Paste);
        _sessionModifier = Combo("Session wechseln: Tasten plus 1 bis 9", ShortcutRules.SessionModifiers.Select(m => m + "+1 … 9"), shortcuts.SessionModifier + "+1 … 9");

        _windowModifier = Combo("tmux-Fenster der aktuellen Session: Tasten plus 0 bis 9", ShortcutRules.WindowModifiers.Select(m => m + "+0 … 9"), shortcuts.WindowModifier + "+0 … 9");
        _windowModifier.ToolTip = "Wechselt per tmux-Befehl, ohne dass ein Zeichen im Programm ankommt. Ctrl+0 bis 9 hatten im Terminal eine seltene Altbedeutung (Ctrl+3 ist Escape); Alt+Shift vermeidet das.";
        _nextShortcut = ShortcutField("Nächstes tmux-Fenster", shortcuts.NextWindow, new ShortcutOptions().NextWindow);
        _previousShortcut = ShortcutField("Voriges tmux-Fenster", shortcuts.PreviousWindow, new ShortcutOptions().PreviousWindow);
        _backShortcut = ShortcutField("Zurück zum zuletzt benutzten Fenster (auch auf einem anderen Server)", shortcuts.Back, new ShortcutOptions().Back);
        _paletteShortcut = ShortcutField("Befehlspalette öffnen (nicht Ctrl+K: das ist in der Shell „bis Zeilenende löschen“)", shortcuts.Palette, new ShortcutOptions().Palette);
        var favoritesButton = new Button { Content = $"Favoriten verwalten … ({_favorites.Count})", ToolTip = "tmux-Fenster mit eigenem Tastenkürzel", HorizontalAlignment = HorizontalAlignment.Left };
        favoritesButton.Click += (_, _) =>
        {
            var dialog = new FavoritesDialog(_favorites, _original.Profiles, recentWindows ?? []) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result) { _favorites = result; favoritesButton.Content = $"Favoriten verwalten … ({_favorites.Count})"; }
        };
        _panel.Children.Add(favoritesButton);

        Heading("Clipboard");
        _historyLimit = Field("Ungeheftete Einträge im Verlauf (1 bis 1000)", settings.ClipHistoryLimit.ToString());
        _acceptOsc52 = new CheckBox { Content = "Kopieren aus Programmen übernehmen (OSC 52, z. B. Markieren in Claude Code oder tmux)", IsChecked = settings.AcceptOsc52,
            ToolTip = "Programme in der Session können so Text in die Zwischenablage legen. Lesen der Zwischenablage ist nie möglich." };
        _panel.Children.Add(_acceptOsc52);

        Heading("Diktat");
        _engine = Combo("Spracherkennung (Engine)", SpeechEngineIds.All.Select(SpeechEngineIds.Label), SpeechEngineIds.Label(dictation.Engine));
        _engine.ToolTip = "Whisper läuft lokal auf der Grafikkarte, ohne Schlüssel und ohne Dienst. Die Web Speech API und die Windows-Spracherkennung sind zum Vergleich da; sie laufen auf diesem System nur mit Einschränkungen (siehe docs/dictation-engines.md).";
        _microphone = Combo("Mikrofon", MicrophoneNames(), dictation.Microphone is { Length: > 0 } m ? m : DefaultMicrophone);
        _model = Combo("Modell (wird beim ersten Diktat nach Rückfrage geladen)", WhisperModels.All.Select(ModelLabel), ModelLabel(WhisperModels.Get(dictation.Model)));
        _interimModel = Combo("Schnelles Modell für den Zwischenstand beim Sprechen", WhisperModels.All.Select(ModelLabel), ModelLabel(WhisperModels.Get(dictation.InterimModel)));
        _language = Combo("Sprache", ["Deutsch", "Englisch", "Automatisch erkennen"], dictation.Language switch { "en" => "Englisch", "auto" => "Automatisch erkennen", _ => "Deutsch" });
        _prompt = Field("Fachbegriffe für die Erkennung (Initial-Prompt)", dictation.InitialPrompt);
        _prompt.AcceptsReturn = true; _prompt.TextWrapping = TextWrapping.Wrap; _prompt.Height = 70; _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _silence = Field("Stilledauer bis zum Ende der Aufnahme in Sekunden (0,5 bis 10)", dictation.SilenceSeconds.ToString(CultureInfo.CurrentCulture));
        _threshold = Field("Pegelschwelle für Sprache (0,002 bis 0,2; Vorgabe 0,015, höher = unempfindlicher)", dictation.LevelThreshold.ToString(CultureInfo.CurrentCulture));

        _panel.Children.Add(new TextBlock {
            Text = $"TerminalMgr {AppInfo.Version} · Einstellungen, angeheftete Clipboard-Einträge und Modelle: {AppPaths.DataDirectory()}",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 18, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _panel.Children.Add(buttons);
        var save = new Button { Content = "Speichern", IsDefault = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
    }

    private void Heading(string text) => _panel.Children.Add(new TextBlock {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _panel.Children.Count == 0 ? 0 : 18, 0, 8) });

    private TextBox Field(string label, string value)
    {
        _panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = value };
        _panel.Children.Add(box);
        return box;
    }

    /// <summary>A read-only box that records the pressed combination, a reset button and a note when the key is also used in the terminal.</summary>
    private TextBox ShortcutField(string label, string value, string standard)
    {
        _panel.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = value, IsReadOnly = true, IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 4, 0, 2) };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Goldenrod, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
        void ShowHint() => hint.Text = Shortcut.TryParse(box.Text, out var s) ? ShortcutRules.TerminalHint(s) ?? "" : "";
        box.TextChanged += (_, _) => ShowHint();
        box.PreviewKeyDown += CaptureShortcut;
        var reset = new Button { Content = "Standard", ToolTip = standard, Margin = new Thickness(6, 0, 0, 0) };
        reset.Click += (_, _) => box.Text = standard;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reset, Dock.Right);
        row.Children.Add(reset);
        row.Children.Add(box);
        _panel.Children.Add(row);
        _panel.Children.Add(hint);
        ShowHint();
        return box;
    }

    private ComboBox Combo(string label, IEnumerable<string> items, string selected)
    {
        _panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var box = new ComboBox { Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(8), ItemsSource = items.ToList() };
        box.SelectedItem = box.Items.Cast<string>().FirstOrDefault(i => i == selected) ?? box.Items.Cast<string>().FirstOrDefault();
        _panel.Children.Add(box);
        return box;
    }

    private static string ModelLabel(WhisperModel model) =>
        $"{model.Name} · {model.SizeText} · {model.Description}" + (WhisperModels.IsInstalled(model) ? " · geladen" : "");

    private static IEnumerable<string> MicrophoneNames()
    {
        var names = new List<string> { DefaultMicrophone };
        try { names.AddRange(AudioDevices.Names()); } catch (Exception) { /* no audio subsystem: only the default entry */ }
        return names;
    }

    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var pressed = ShortcutKeys.FromEvent(e);
        if (pressed is null) return; // only a modifier so far
        if (!Shortcut.TryParse(pressed.ToString(), out var shortcut))
        {
            Warn("Bitte mit Ctrl oder Alt kombinieren (nur F1 bis F12 gehen allein), damit normales Tippen nicht blockiert wird.");
            return;
        }
        ((TextBox)sender).Text = shortcut.ToString();
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void Save()
    {
        if (!int.TryParse(_historyLimit.Text.Trim(), out int limit) || limit is < 1 or > 1000)
        { Warn("Der Verlauf muss zwischen 1 und 1000 Einträge groß sein."); return; }
        if (!TryNumber(_silence.Text, out double silence) || silence is < 0.5 or > 10)
        { Warn("Die Stilledauer muss zwischen 0,5 und 10 Sekunden liegen."); return; }
        if (!TryNumber(_threshold.Text, out double threshold) || threshold is < 0.002 or > 0.2)
        { Warn("Die Pegelschwelle muss zwischen 0,002 und 0,2 liegen."); return; }
        string microphone = (string)_microphone.SelectedItem;
        var candidate = _original with {
            ClipHistoryLimit = limit,
            StartWithWindows = _startWithWindows.IsChecked == true,
            AcceptOsc52 = _acceptOsc52.IsChecked == true,
            Update = new UpdateOptions { CheckOnStart = _checkUpdates.IsChecked == true, SkippedVersion = _skippedVersion },
            Shortcuts = new ShortcutOptions {
                Copy = _copyShortcut.Text, Paste = _pasteShortcut.Text,
                SessionModifier = ShortcutRules.SessionModifiers[Math.Max(0, _sessionModifier.SelectedIndex)],
                WindowModifier = ShortcutRules.WindowModifiers[Math.Max(0, _windowModifier.SelectedIndex)],
                NextWindow = _nextShortcut.Text, PreviousWindow = _previousShortcut.Text, Back = _backShortcut.Text, Palette = _paletteShortcut.Text
            }.Normalized(),
            Favorites = _favorites,
            Dictation = (_original.Dictation with {
                Shortcut = _dictationShortcut.Text,
                Microphone = microphone == DefaultMicrophone ? null : microphone,
                Engine = SpeechEngineIds.All[Math.Max(0, _engine.SelectedIndex)],
                Model = WhisperModels.All[Math.Max(0, _model.SelectedIndex)].Name,
                InterimModel = WhisperModels.All[Math.Max(0, _interimModel.SelectedIndex)].Name,
                Language = _language.SelectedIndex switch { 1 => "en", 2 => "auto", _ => "de" },
                InitialPrompt = _prompt.Text.Trim(),
                SilenceSeconds = silence,
                LevelThreshold = threshold
            }).Normalized()
        };
        if (ShortcutRules.FindConflict(candidate) is { } conflict) { Warn(conflict); return; }
        Result = candidate;
        DialogResult = true;
    }

    private void Warn(string message) => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
}
