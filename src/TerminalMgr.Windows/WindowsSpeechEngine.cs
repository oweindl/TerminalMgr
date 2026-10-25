using TerminalMgr.Core;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace TerminalMgr.Windows;

/// <summary>
/// Windows.Media.SpeechRecognition (the dictation of Windows itself). It records from the default microphone on its own and needs no key
/// or Azure resource, but Windows only lets it run when "Online speech recognition" is switched on in the privacy settings,
/// and then the audio goes to Microsoft's service. Without that setting starting fails with 0x80045509; this engine reports
/// that as <see cref="SpeechUnavailableException"/> and does not touch the setting.
/// </summary>
public sealed class WindowsSpeechEngine(DictationOptions options) : ISpeechEngine
{
    private const int PrivacyPolicyNotAccepted = unchecked((int)0x80045509);
    private readonly object _lock = new();
    private readonly List<string> _finals = [];
    private SpeechRecognizer? _recognizer;
    private DictationState _state;
    private bool _manual;
    private int _generation;
    private DateTime _started;

    public string Id => SpeechEngineIds.Windows;
    public DictationMetrics Metrics { get; private set; } = new();
    public DictationState State { get { lock (_lock) return _state; } }
    public event Action<DictationState>? StateChanged;
#pragma warning disable CS0067 // the Windows recognizer records on its own and reports no input level
    public event Action<double>? Level;
#pragma warning restore CS0067
    public event Action<string, string>? Interim;
    public event Action<DictationResult>? Completed;
    public event Action<Exception>? Failed;

    /// <summary>Whether the recognizer can be created and its constraints compiled; the privacy setting itself only shows when recording starts.</summary>
    public static async Task<string?> CheckAsync(DictationOptions options)
    {
        try
        {
            using var recognizer = Create(options);
            var compiled = await recognizer.CompileConstraintsAsync();
            return compiled.Status == SpeechRecognitionResultStatus.Success ? null : "Die Sprache ist für die Windows-Spracherkennung nicht eingerichtet: " + compiled.Status;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static SpeechRecognizer Create(DictationOptions options)
    {
        string tag = options.Language switch { "en" => "en-US", "auto" => SpeechRecognizer.SystemSpeechLanguage.LanguageTag, _ => "de-DE" };
        var recognizer = new SpeechRecognizer(new Language(tag));
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        return recognizer;
    }

    public void Start()
    {
        int generation;
        lock (_lock)
        {
            if (_state != DictationState.Idle) return;
            _finals.Clear();
            _manual = false;
            Metrics = new();
            generation = ++_generation;
            _state = DictationState.Recording;
            _started = DateTime.UtcNow;
        }
        StateChanged?.Invoke(DictationState.Recording);
        _ = Task.Run(() => StartAsync(generation));
    }

    private async Task StartAsync(int generation)
    {
        try
        {
            var recognizer = Create(options);
            lock (_lock) { if (generation != _generation) { recognizer.Dispose(); return; } _recognizer = recognizer; }
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
                throw new SpeechUnavailableException("Die Windows-Spracherkennung kennt die Sprache nicht: " + compiled.Status);
            var session = recognizer.ContinuousRecognitionSession;
            session.AutoStopSilenceTimeout = TimeSpan.FromSeconds(Math.Clamp(options.SilenceSeconds + 1, 2, 20));
            recognizer.HypothesisGenerated += (_, e) => OnHypothesis(generation, e.Hypothesis.Text);
            session.ResultGenerated += (_, e) => OnResult(generation, e.Result);
            session.Completed += (_, e) => OnCompleted(generation, e.Status);
            await session.StartAsync();
            Metrics.SpeechStarted();
        }
        catch (Exception ex) when (ex.HResult == PrivacyPolicyNotAccepted)
        {
            Fail(generation, new SpeechUnavailableException(
                "Die Windows-Spracherkennung ist ausgeschaltet. Sie läuft nur, wenn unter Windows-Einstellungen › Datenschutz › Spracherkennung " +
                "„Onlinespracherkennung“ eingeschaltet ist; dann geht das Audio an den Microsoft-Dienst. TerminalMgr ändert diese Einstellung nicht."));
        }
        catch (Exception ex) { Fail(generation, ex); }
    }

    private void OnHypothesis(int generation, string text)
    {
        string confirmed;
        lock (_lock)
        {
            if (generation != _generation || _state != DictationState.Recording) return;
            confirmed = string.Join(' ', _finals);
        }
        Metrics.InterimArrived();
        Interim?.Invoke(confirmed, text);
    }

    private void OnResult(int generation, SpeechRecognitionResult result)
    {
        if (result.Status != SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(result.Text)) return;
        string confirmed;
        lock (_lock)
        {
            if (generation != _generation) return;
            _finals.Add(result.Text.Trim());
            confirmed = string.Join(' ', _finals);
        }
        Metrics.InterimArrived();
        Interim?.Invoke(confirmed, "");
    }

    private void OnCompleted(int generation, SpeechRecognitionResultStatus status)
    {
        string text;
        bool manual;
        lock (_lock)
        {
            if (generation != _generation || _state == DictationState.Idle) return;
            _state = DictationState.Idle;
            text = DictationText.Clean(string.Join(' ', _finals));
            manual = _manual;
            Metrics.FinalArrived();
        }
        Release(generation);
        StateChanged?.Invoke(DictationState.Idle);
        if (status is SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.TimeoutExceeded or SpeechRecognitionResultStatus.UserCanceled || text.Length > 0)
            Completed?.Invoke(new(text, manual ? StopReason.None : StopReason.Silence, manual));
        else Failed?.Invoke(new InvalidOperationException("Die Windows-Spracherkennung wurde beendet: " + status));
    }

    public void Stop()
    {
        SpeechRecognizer? recognizer;
        lock (_lock)
        {
            if (_state != DictationState.Recording) return;
            _state = DictationState.Processing;
            _manual = true;
            recognizer = _recognizer;
            Metrics.StopRequested((DateTime.UtcNow - _started).TotalSeconds);
        }
        StateChanged?.Invoke(DictationState.Processing);
        _ = Task.Run(async () =>
        {
            try { if (recognizer is not null) await recognizer.ContinuousRecognitionSession.StopAsync(); }
            catch (Exception) { /* the Completed event or the failure path ends the round */ }
        });
    }

    public void Cancel()
    {
        SpeechRecognizer? recognizer;
        lock (_lock)
        {
            if (_state == DictationState.Idle) return;
            _generation++;
            recognizer = _recognizer;
            _recognizer = null;
            _state = DictationState.Idle;
        }
        StateChanged?.Invoke(DictationState.Idle);
        _ = Task.Run(async () =>
        {
            try { if (recognizer is not null) { await recognizer.ContinuousRecognitionSession.CancelAsync(); recognizer.Dispose(); } }
            catch (Exception) { }
        });
    }

    private void Fail(int generation, Exception ex)
    {
        lock (_lock)
        {
            if (generation != _generation || _state == DictationState.Idle) return;
            _state = DictationState.Idle;
        }
        Release(generation);
        StateChanged?.Invoke(DictationState.Idle);
        Failed?.Invoke(ex);
    }

    private void Release(int generation)
    {
        SpeechRecognizer? recognizer;
        lock (_lock) { if (generation != _generation) return; recognizer = _recognizer; _recognizer = null; }
        if (recognizer is not null) _ = Task.Run(() => { try { recognizer.Dispose(); } catch (Exception) { } });
    }

    public void Dispose() => Cancel();
}
