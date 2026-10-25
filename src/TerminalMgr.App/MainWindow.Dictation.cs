using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Dictation: panel above the terminal, record button, model consent, hand-over of the text to the session.</summary>
public partial class MainWindow
{
    private ISpeechEngine? _dictation;
    private WebSpeechEngine? _webSpeech;
    private WhisperTranscriber? _transcriber, _interimTranscriber;
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
        DictationTitle.Text = "Diktat → " + target.Name + " · " + SpeechEngineIds.Label(DictationSettings.Engine);
        DictationBox.Text = "";
        DictationPartial.Visibility = Visibility.Collapsed;
        DictationStatus.Text = "Bereit";
        // The panel lives in the right column; a hidden clipboard column comes back with it.
        if (ClipboardColumn.Width.Value <= 0) ToggleClipboard(this, new RoutedEventArgs());
        DictationPanel.Visibility = Visibility.Visible;
        UpdateDictationHeight();
        var options = DictationSettings;
        if (options.Engine == SpeechEngineIds.Whisper)
        {
            if (!await EnsureModelAsync(options.Model)) { CloseDictation(); return; }
            // The small model for the provisional text is optional: without it the final model is asked again every second or so (slow).
            if (_dictationOpen && options.InterimModel != options.Model) await EnsureModelAsync(options.InterimModel);
            if (_dictationOpen) _ = WarmUpWhisperAsync(options);
        }
        if (_dictationOpen) StartRecording();
    }

    /// <summary>Loads the models and compiles the GPU kernels while the user starts talking, so that the first words are not delayed.</summary>
    private async Task WarmUpWhisperAsync(DictationOptions options)
    {
        try
        {
            _transcriber ??= new WhisperTranscriber();
            _interimTranscriber ??= new WhisperTranscriber(interim: true);
            if (WhisperModels.IsInstalled(WhisperModels.Get(options.InterimModel))) await _interimTranscriber.PrepareAsync(options);
            await _transcriber.PrepareAsync(options);
        }
        catch (Exception ex) { StatusText.Text = "Diktat: Modell konnte nicht vorbereitet werden: " + ex.Message; }
    }

    /// <summary>Panel height: at most a third of the window; the text inside scrolls.</summary>
    private void UpdateDictationHeight() => DictationPanel.MaxHeight = DictationLayout.MaxPanelHeight(ActualHeight);

    private void DictationBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => DictationScroll.ScrollToEnd();

    /// <summary>The model is never downloaded silently: the user is asked first and sees the progress.</summary>
    private async Task<bool> EnsureModelAsync(string modelName)
    {
        var model = WhisperModels.Get(modelName);
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
        var session = _dictation = CreateEngine(options);
        // Engine events come from worker threads; only the current engine may touch the UI.
        void Ui(Action action) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_dictation, session) && !_closing) action(); });
        session.StateChanged += state => Ui(() => ApplyDictationState(state));
        session.Level += level => Ui(() => DictationLevel.Value = level * 100);
        session.Interim += (confirmed, tentative) => Ui(() => ShowInterim(confirmed, tentative));
        session.Completed += result => Ui(() => DictationCompleted(result));
        session.Failed += ex => Ui(() => DictationFailed(ex));
        _dictationCaret = Math.Clamp(DictationBox.SelectionStart, 0, DictationBox.Text.Length);
        DictationPartial.Visibility = Visibility.Collapsed;
        session.Start();
    }

    /// <summary>The recognizer chosen in the settings; Whisper falls back to the whole-segment provisional text when the small model is missing.</summary>
    private ISpeechEngine CreateEngine(DictationOptions options)
    {
        _webSpeech = null;
        switch (options.Engine)
        {
            case SpeechEngineIds.WebSpeech:
                return _webSpeech = new WebSpeechEngine(Post, options);
            case SpeechEngineIds.Windows:
                return new WindowsSpeechEngine(options);
            default:
                _transcriber ??= new WhisperTranscriber();
                _interimTranscriber ??= new WhisperTranscriber(interim: true);
                bool smallModel = options.InterimModel != options.Model && WhisperModels.IsInstalled(WhisperModels.Get(options.InterimModel));
                return new DictationSession(() => new MicrophoneSource(options.Microphone), _transcriber, options, interim: smallModel ? _interimTranscriber : null);
        }
    }

    /// <summary>Confirmed words in the normal color, the tentative rest in italics: they may still change.</summary>
    private void ShowInterim(string confirmed, string tentative)
    {
        DictationPartial.Inlines.Clear();
        if (confirmed.Length > 0) DictationPartial.Inlines.Add(new Run(confirmed + " ") { Foreground = DictationBox.Foreground, FontStyle = FontStyles.Normal });
        if (tentative.Length > 0) DictationPartial.Inlines.Add(new Run(tentative));
        DictationPartial.Visibility = confirmed.Length + tentative.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DictationScroll.ScrollToEnd();
    }

    private void DictationFailed(Exception ex)
    {
        DictationStatus.Text = ex is SpeechUnavailableException ? "Engine nicht verfügbar" : "Fehler: " + ex.Message;
        StatusText.Text = "Diktat: " + ex.Message;
        if (ex is SpeechUnavailableException)
            MessageBox.Show(this, ex.Message + "\n\nIn den Einstellungen unter „Diktat“ kann die Engine „Whisper (lokal, GPU)“ gewählt werden; sie braucht weder Dienst noch Schlüssel.",
                "Diktat", MessageBoxButton.OK, MessageBoxImage.Information);
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
        LogSpeechMetrics(result);
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

    /// <summary>Measuring mode in the app (TERMINALMGR_SPEECH_METRICS=1): timings of each round go to the status line and to speech-metrics.log.</summary>
    private void LogSpeechMetrics(DictationResult result)
    {
        if (Environment.GetEnvironmentVariable("TERMINALMGR_SPEECH_METRICS") != "1" || _dictation is not { } engine) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {engine.Id}: {engine.Metrics.Summary()} · {result.Text.Length} Zeichen";
        StatusText.Text = "Diktat: " + line;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(_dataDirectory, "speech-metrics.log"), line + Environment.NewLine); }
        catch (System.IO.IOException) { /* diagnostics only */ }
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
        _interimTranscriber?.Dispose();
    }
}
