using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Dictation: panel above the terminal, record button, model consent, hand-over of the text to the session.</summary>
public partial class MainWindow
{
    private DictationSession? _dictation;
    private WhisperTranscriber? _transcriber;
    private string? _dictationTargetId;
    private int _dictationCaret;
    private CancellationTokenSource? _modelDownload;
    private bool _dictationOpen;

    private DictationOptions DictationSettings => _settings.Dictation.Normalized();

    /// <summary>Switches of the terminal page: OSC 52 clipboard writes, and diagnostics for right clicks (TERMINALMGR_DEBUG=1).</summary>
    private void PostTerminalSettings() =>
        Post(new { type = "terminalSettings", osc52 = _settings.AcceptOsc52, debug = Environment.GetEnvironmentVariable("TERMINALMGR_DEBUG") == "1" });

    /// <summary>Hands the configured shortcuts to the terminal page, which takes them before the session sees them.</summary>
    private void PostShortcuts()
    {
        var options = _settings.Shortcuts.Normalized();
        Post(new {
            type = "shortcuts",
            shortcuts = new Dictionary<string, string> { ["dictation"] = DictationSettings.Shortcut, ["copy"] = options.Copy, ["paste"] = options.Paste },
            sessionModifier = options.SessionModifier
        });
    }

    private void DictationButtonClick(object sender, RoutedEventArgs e) => ToggleDictation();
    private void DictationRecordClick(object sender, RoutedEventArgs e) => ToggleDictation();

    /// <summary>Ctrl+Y (or the button): open and record; while recording stop; when the text stands, record again.</summary>
    private void ToggleDictation()
    {
        if (_closing) return;
        if (!_dictationOpen) { _ = OpenDictationAsync(); return; }
        if (_modelDownload is not null) return;
        switch (_dictation?.State ?? DictationState.Idle)
        {
            case DictationState.Recording: _dictation!.Stop(); break;
            case DictationState.Idle: StartRecording(); break;
        }
    }

    private async Task OpenDictationAsync()
    {
        if (Selected is not { } target) { StatusText.Text = "Zum Diktieren zuerst eine Session auswählen."; return; }
        _dictationOpen = true;
        _dictationTargetId = target.Profile.Id; // later server switches do not change the target
        DictationTitle.Text = "Diktat → " + target.Name;
        DictationBox.Text = "";
        DictationPartial.Visibility = Visibility.Collapsed;
        DictationStatus.Text = "Bereit";
        DictationPanel.Visibility = Visibility.Visible;
        if (!await EnsureModelAsync()) { CloseDictation(); return; }
        if (_dictationOpen) StartRecording();
    }

    /// <summary>The model is never downloaded silently: the user is asked first and sees the progress.</summary>
    private async Task<bool> EnsureModelAsync()
    {
        var model = WhisperModels.Get(DictationSettings.Model);
        if (WhisperModels.IsInstalled(model)) return true;
        var answer = MessageBox.Show(this,
            $"Für das Diktat wird einmalig das Spracherkennungsmodell „{model.Name}“ ({model.SizeText}) benötigt.\n\n" +
            $"Es wird von huggingface.co nach\n{AppPaths.ModelsDirectory()}\ngeladen. Danach läuft die Spracherkennung vollständig lokal; Audio verlässt den Rechner nicht.\n\nJetzt herunterladen?",
            "Diktat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        var cts = _modelDownload = new CancellationTokenSource();
        DictationDownload.Visibility = Visibility.Visible;
        DictationDownloadBar.Value = 0;
        DictationDownloadText.Text = $"Modell „{model.Name}“ wird geladen …";
        DictationStatus.Text = "Modell wird geladen";
        try
        {
            var progress = new Progress<(long Done, long Total)>(p => {
                DictationDownloadBar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                DictationDownloadText.Text = $"Modell „{model.Name}“ wird geladen … {p.Done / 1_000_000} von {p.Total / 1_000_000} MB";
            });
            await ModelDownloader.DownloadAsync(model, AppPaths.ModelsDirectory(), progress, cts.Token);
            return true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Modell-Download abgebrochen."; return false; }
        catch (Exception ex) { ShowError("Das Modell konnte nicht geladen werden.", ex); return false; }
        finally { DictationDownload.Visibility = Visibility.Collapsed; _modelDownload = null; }
    }

    private void DictationDownloadCancel(object sender, RoutedEventArgs e) => _modelDownload?.Cancel();

    private void StartRecording()
    {
        _dictation?.Dispose();
        var options = DictationSettings;
        _transcriber ??= new WhisperTranscriber();
        var transcriber = _transcriber;
        var session = _dictation = new DictationSession(() => new MicrophoneSource(options.Microphone), transcriber, options);
        // Session events come from worker threads; only the current session may touch the UI.
        void Ui(Action action) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_dictation, session) && !_closing) action(); });
        session.StateChanged += state => Ui(() => ApplyDictationState(state));
        session.Level += level => Ui(() => DictationLevel.Value = level * 100);
        session.Partial += text => Ui(() => {
            DictationPartial.Text = "… " + text;
            DictationPartial.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        session.Completed += result => Ui(() => DictationCompleted(result));
        session.Failed += ex => Ui(() => {
            DictationStatus.Text = "Fehler: " + ex.Message;
            StatusText.Text = "Diktat: " + ex.Message;
        });
        _dictationCaret = Math.Clamp(DictationBox.SelectionStart, 0, DictationBox.Text.Length);
        DictationPartial.Visibility = Visibility.Collapsed;
        session.Start();
    }

    private void ApplyDictationState(DictationState state)
    {
        switch (state)
        {
            case DictationState.Recording:
                DictationStatus.Text = "Aufnahme …";
                DictationRecordButton.Content = "■ Stopp";
                DictationRecordButton.IsEnabled = true;
                DictationButton.Content = "● Aufnahme";
                DictationButton.Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x3A, 0x2E));
                DictationButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.55, TimeSpan.FromMilliseconds(650)) {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                break;
            case DictationState.Processing:
                DictationStatus.Text = "Transkribiere …";
                DictationRecordButton.IsEnabled = false;
                DictationButton.BeginAnimation(OpacityProperty, null);
                DictationButton.Content = "Verarbeite …";
                DictationButton.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x1F));
                DictationLevel.Value = 0;
                break;
            default:
                DictationButton.BeginAnimation(OpacityProperty, null);
                DictationButton.ClearValue(BackgroundProperty);
                DictationButton.Content = "🎤 Diktat";
                DictationRecordButton.Content = "● Aufnahme";
                DictationRecordButton.IsEnabled = true;
                DictationLevel.Value = 0;
                if (_dictationOpen) DictationStatus.Text = "Bereit";
                break;
        }
    }

    private void DictationCompleted(DictationResult result)
    {
        DictationPartial.Visibility = Visibility.Collapsed;
        if (result.Text.Length > 0)
        {
            // New text goes where the cursor was; the final text replaces the provisional one.
            var (text, caret) = DictationText.InsertAtCaret(DictationBox.Text, _dictationCaret, result.Text);
            DictationBox.Text = text;
            DictationBox.CaretIndex = caret;
            DictationStatus.Text = result.Manual ? "Bereit" : "Stille erkannt · " + DictationSettings.Shortcut + " setzt fort";
        }
        else DictationStatus.Text = "Keine Sprache erkannt";
        if (IsActive) DictationBox.Focus();
    }

    private void DictationBoxKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key is Key.Return or Key.Enter)
        {
            if (modifiers.HasFlag(ModifierKeys.Control)) { DictationSubmit(run: true); e.Handled = true; }
            else if (!modifiers.HasFlag(ModifierKeys.Shift)) { DictationSubmit(run: false); e.Handled = true; }
            // Shift+Enter falls through: the text box inserts a line break.
        }
        else if (e.Key == Key.Escape)
        {
            // A running recording is canceled first; only then Esc discards the dictation.
            if (_dictation?.State is DictationState.Recording or DictationState.Processing)
            {
                _dictation.Cancel();
                DictationPartial.Visibility = Visibility.Collapsed;
                DictationStatus.Text = "Aufnahme abgebrochen";
            }
            else CloseDictation();
            e.Handled = true;
        }
    }

    private void DictationInsert(object sender, RoutedEventArgs e) => DictationSubmit(run: false);
    private void DictationInsertRun(object sender, RoutedEventArgs e) => DictationSubmit(run: true);
    private void DictationDiscard(object sender, RoutedEventArgs e) => CloseDictation();

    private void DictationSubmit(bool run)
    {
        string text = DictationText.ForPaste(DictationBox.Text);
        if (text.Length == 0) { DictationStatus.Text = "Kein Text zum Einfügen"; return; }
        if (_dictationTargetId is not { } id || !_sessions.ContainsKey(id))
        {
            DictationStatus.Text = "Die Zielsession ist nicht verbunden";
            StatusText.Text = "Diktat: Zielsession nicht verbunden. Der Text bleibt im Diktatfenster stehen.";
            return;
        }
        // Paste goes through xterm.js, so bracketed paste applies when the program asked for it (Claude Code, bash, tmux).
        Post(new { type = "paste", id, text, enter = run });
        if (_profiles.FirstOrDefault(p => p.Profile.Id == id) is { } target) ProfilesList.SelectedItem = target;
        CloseDictation();
        Post(new { type = "focus", id });
    }

    private void CloseDictation()
    {
        _modelDownload?.Cancel();
        _dictation?.Cancel();
        _dictationOpen = false;
        _dictationTargetId = null;
        DictationPanel.Visibility = Visibility.Collapsed;
        DictationPartial.Visibility = Visibility.Collapsed;
        DictationBox.Text = "";
        ApplyDictationState(DictationState.Idle);
        if (Selected is { } selected) Post(new { type = "focus", id = selected.Profile.Id });
    }

    private void DisposeDictation()
    {
        _modelDownload?.Cancel();
        _dictation?.Dispose();
        _transcriber?.Dispose();
    }
}
