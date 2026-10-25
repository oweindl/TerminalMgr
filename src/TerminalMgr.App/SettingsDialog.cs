using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Application settings. Sections are added by the features that own them.</summary>
public sealed class SettingsDialog : Window
{
    private const string DefaultMicrophone = "Windows-Standard";
    private readonly AppSettings _original;
    private readonly StackPanel _panel = new() { Margin = new Thickness(24) };
    private readonly TextBox _historyLimit, _shortcut, _prompt, _silence, _threshold;
    private readonly ComboBox _microphone, _model, _language;
    public AppSettings? Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _original = settings;
        var dictation = settings.Dictation.Normalized();
        Style = (Style)FindResource(typeof(Window));
        Title = "Einstellungen";
        Width = 600; Height = 780; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Heading("Clipboard");
        _historyLimit = Field("Ungeheftete Einträge im Verlauf (1 bis 1000)", settings.ClipHistoryLimit.ToString());

        Heading("Diktat");
        _panel.Children.Add(new TextBlock { Text = "Tastenkürzel (Feld anklicken und Kombination drücken; mit Ctrl oder Alt)" });
        _shortcut = new TextBox { Text = dictation.Shortcut, IsReadOnly = true, IsReadOnlyCaretVisible = false };
        _shortcut.PreviewKeyDown += CaptureShortcut;
        var shortcutRow = new DockPanel { LastChildFill = true };
        var reset = new Button { Content = "Standard", ToolTip = "Ctrl+Y" };
        reset.Click += (_, _) => _shortcut.Text = new DictationOptions().Shortcut;
        DockPanel.SetDock(reset, Dock.Right);
        shortcutRow.Children.Add(reset);
        shortcutRow.Children.Add(_shortcut);
        _panel.Children.Add(shortcutRow);
        _panel.Children.Add(new TextBlock { Text = "In bash/readline ist Ctrl+Y „yank“. Mit der Standardbelegung ist es in TerminalMgr nicht erreichbar; hier lässt sich das ändern.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });

        _microphone = Combo("Mikrofon", MicrophoneNames(), dictation.Microphone is { Length: > 0 } m ? m : DefaultMicrophone);
        _model = Combo("Modell (wird beim ersten Diktat nach Rückfrage geladen)", WhisperModels.All.Select(ModelLabel), ModelLabel(WhisperModels.Get(dictation.Model)));
        _language = Combo("Sprache", ["Deutsch", "Englisch", "Automatisch erkennen"], dictation.Language switch { "en" => "Englisch", "auto" => "Automatisch erkennen", _ => "Deutsch" });
        _prompt = Field("Fachbegriffe für die Erkennung (Initial-Prompt)", dictation.InitialPrompt);
        _prompt.AcceptsReturn = true; _prompt.TextWrapping = TextWrapping.Wrap; _prompt.Height = 70; _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _silence = Field("Stilledauer bis zum Ende der Aufnahme in Sekunden (0,5 bis 10)", dictation.SilenceSeconds.ToString(CultureInfo.CurrentCulture));
        _threshold = Field("Pegelschwelle für Sprache (0,002 bis 0,2; Vorgabe 0,015, höher = unempfindlicher)", dictation.LevelThreshold.ToString(CultureInfo.CurrentCulture));

        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
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
            MessageBox.Show(this, "Bitte mit Ctrl oder Alt kombinieren (nur F1 bis F12 gehen allein), damit normales Tippen nicht blockiert wird.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _shortcut.Text = shortcut.ToString();
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
        Result = _original with {
            ClipHistoryLimit = limit,
            Dictation = new DictationOptions {
                Shortcut = _shortcut.Text,
                Microphone = microphone == DefaultMicrophone ? null : microphone,
                Model = WhisperModels.All[Math.Max(0, _model.SelectedIndex)].Name,
                Language = _language.SelectedIndex switch { 1 => "en", 2 => "auto", _ => "de" },
                InitialPrompt = _prompt.Text.Trim(),
                SilenceSeconds = silence,
                LevelThreshold = threshold
            }.Normalized()
        };
        DialogResult = true;
    }

    private void Warn(string message) => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
}
