using System.Text.Json;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// The Web Speech API inside the WebView2 of the terminal page. The page records and recognizes on its own; this class only starts and
/// stops it and turns its messages into the common events. In WebView2 the browser's speech service is missing, so starting ends
/// with the error "network" (measured, see docs/dictation-engines.md); that is reported as <see cref="SpeechUnavailableException"/>.
/// </summary>
public sealed class WebSpeechEngine(Action<object> post, DictationOptions options) : ISpeechEngine
{
    private readonly object _lock = new();
    private readonly SortedDictionary<int, string> _finals = [];
    private string _interimText = "";
    private DictationState _state;
    private bool _manual, _failed;
    private DateTime _started;

    public string Id => SpeechEngineIds.WebSpeech;
    public DictationMetrics Metrics { get; private set; } = new();
    public DictationState State { get { lock (_lock) return _state; } }
    public event Action<DictationState>? StateChanged;
#pragma warning disable CS0067 // the page records on its own and reports no input level
    public event Action<double>? Level;
#pragma warning restore CS0067
    public event Action<string, string>? Interim;
    public event Action<DictationResult>? Completed;
    public event Action<Exception>? Failed;

    public void Start()
    {
        lock (_lock)
        {
            if (_state != DictationState.Idle) return;
            _finals.Clear();
            _interimText = "";
            _manual = _failed = false;
            Metrics = new();
            _state = DictationState.Recording;
            _started = DateTime.UtcNow;
        }
        StateChanged?.Invoke(DictationState.Recording);
        string language = options.Language switch { "en" => "en-US", "auto" => "", _ => "de-DE" };
        post(new { type = "speech", cmd = "start", lang = language });
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_state != DictationState.Recording) return;
            _state = DictationState.Processing;
            _manual = true;
            Metrics.StopRequested((DateTime.UtcNow - _started).TotalSeconds);
        }
        StateChanged?.Invoke(DictationState.Processing);
        post(new { type = "speech", cmd = "stop" });
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (_state == DictationState.Idle) return;
            _state = DictationState.Idle;
            _failed = true; // the end message of the aborted page recognizer is not a result
        }
        post(new { type = "speech", cmd = "abort" });
        StateChanged?.Invoke(DictationState.Idle);
    }

    /// <summary>A "speech" message from the page: started, interim, final, error or end.</summary>
    public void OnPageMessage(JsonElement m)
    {
        string kind = m.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        switch (kind)
        {
            case "started":
                Metrics.SpeechStarted();
                break;
            case "interim" or "final":
            {
                int index = m.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                string text = (m.TryGetProperty("text", out var t) ? t.GetString() : "")?.Trim() ?? "";
                string confirmed, tentative;
                lock (_lock)
                {
                    if (_state == DictationState.Idle) return;
                    if (kind == "final") { _finals[index] = text; _interimText = ""; }
                    else _interimText = text;
                    confirmed = string.Join(' ', _finals.Values);
                    tentative = _interimText;
                }
                Metrics.InterimArrived();
                Interim?.Invoke(confirmed, tentative);
                break;
            }
            case "error":
            {
                string error = m.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "";
                if (error is "no-speech" or "aborted") break;
                lock (_lock) { if (_state == DictationState.Idle || _failed) return; _failed = true; _state = DictationState.Idle; }
                StateChanged?.Invoke(DictationState.Idle);
                Failed?.Invoke(error switch
                {
                    "network" => new SpeechUnavailableException("Die Web Speech API kann im eingebetteten WebView2 nicht erkennen: Der Spracherkennungsdienst des Browsers ist dort nicht erreichbar (Fehler „network“)."),
                    "not-allowed" or "service-not-allowed" => new SpeechUnavailableException("Die Web Speech API hat keinen Zugriff auf das Mikrofon oder den Dienst (" + error + ")."),
                    "unsupported" => new SpeechUnavailableException("Dieses WebView2 kennt die Web Speech API nicht."),
                    _ => new InvalidOperationException("Web Speech API: " + error)
                });
                break;
            }
            case "end":
            {
                string text;
                bool manual;
                lock (_lock)
                {
                    if (_state == DictationState.Idle || _failed) return;
                    _state = DictationState.Idle;
                    text = DictationText.Clean(string.Join(' ', _finals.Values.Append(_interimText)));
                    manual = _manual;
                    Metrics.FinalArrived();
                }
                StateChanged?.Invoke(DictationState.Idle);
                Completed?.Invoke(new(text, manual ? StopReason.None : StopReason.Silence, manual));
                break;
            }
        }
    }

    public void Dispose() => Cancel();
}
