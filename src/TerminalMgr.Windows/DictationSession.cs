using System.Diagnostics;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <summary>
/// The Whisper engine. One dictation round: record, show a provisional text while speaking, stop on silence, then transcribe
/// the whole segment with the configured model for the final text.
/// With an <see cref="IWordTranscriber"/> the provisional text comes from a sliding window: every cycle recognizes only the
/// latest audio (at most <see cref="DictationOptions.InterimWindowSeconds"/>, starting at the last confirmed word) with the small
/// model, and <see cref="WordAgreement"/> confirms words that two cycles agree on, so they are not computed again.
/// Without it (tests, fallback) the whole segment so far is transcribed again every <c>partialSeconds</c> of audio.
/// Events arrive on worker threads; the UI has to marshal them.
/// </summary>
public sealed class DictationSession(Func<IAudioSource> createSource, ITranscriber transcriber, DictationOptions options, double partialSeconds = 1.75,
    IWordTranscriber? interim = null) : ISpeechEngine
{
    private readonly object _lock = new();
    private readonly List<short> _samples = [];
    private readonly WordAgreement _agreement = new();
    private IAudioSource? _source;
    private SilenceDetector _detector = new();
    private CancellationTokenSource _work = new();
    private DictationState _state;
    private int _generation, _samplesAtPartial;
    private bool _partialRunning, _speechNoted;
    private const double MinimumWindowSeconds = 1.0;
    private const double WindowMargin = 0.2;  // seconds before the last confirmed word, so that a cut word comes back whole

    public string Id => SpeechEngineIds.Whisper;
    public DictationMetrics Metrics { get; private set; } = new();

    public event Action<DictationState>? StateChanged;
    public event Action<double>? Level;
    /// <summary>The provisional text as one string (confirmed and tentative words).</summary>
    public event Action<string>? Partial;
    public event Action<string, string>? Interim;
    public event Action<DictationResult>? Completed;
    public event Action<Exception>? Failed;

    public DictationState State { get { lock (_lock) return _state; } }

    public void Start()
    {
        IAudioSource source;
        int generation;
        lock (_lock)
        {
            if (_state != DictationState.Idle) return;
            _samples.Clear();
            _agreement.Reset();
            _samplesAtPartial = 0;
            _partialRunning = _speechNoted = false;
            Metrics = new();
            _detector = new SilenceDetector(AudioDevices.SampleRate, options.SilenceSeconds, options.LevelThreshold);
            _work = new CancellationTokenSource();
            generation = ++_generation;
            source = _source = createSource();
            _state = DictationState.Recording;
        }
        StateChanged?.Invoke(DictationState.Recording);
        source.Samples += samples => OnSamples(generation, samples);
        source.Failed += ex => Fail(generation, ex);
        try { source.Start(); }
        catch (Exception ex) { Fail(generation, ex); }
    }

    /// <summary>Ends the recording now and transcribes what was said.</summary>
    public void Stop() => Finish(manual: true, StopReason.None, expectedGeneration: null);

    /// <summary>Throws the recording away; no result is reported.</summary>
    public void Cancel()
    {
        IAudioSource? source;
        lock (_lock)
        {
            if (_state == DictationState.Idle) return;
            _generation++;
            _work.Cancel();
            source = _source;
            _source = null;
            _state = DictationState.Idle;
        }
        ReleaseSource(source);
        StateChanged?.Invoke(DictationState.Idle);
    }

    private void OnSamples(int generation, short[] chunk)
    {
        StopReason reason;
        double level;
        short[]? snapshot = null;
        int snapshotStart = 0;
        CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation || _state != DictationState.Recording) return;
            _samples.AddRange(chunk);
            reason = _detector.Feed(chunk);
            level = _detector.Level;
            token = _work.Token;
            if (_detector.SpeechHeard && !_speechNoted) { _speechNoted = true; Metrics.SpeechStarted(); }
            double cycle = interim is null ? partialSeconds : options.InterimCycleSeconds;
            if (reason == StopReason.None && _detector.SpeechHeard && !_partialRunning &&
                _samples.Count - _samplesAtPartial >= cycle * AudioDevices.SampleRate)
            {
                int previousMark = _samplesAtPartial;
                _partialRunning = true;
                _samplesAtPartial = _samples.Count;
                if (interim is null) snapshot = _samples.ToArray();
                else
                {
                    // Only the latest audio: from just before the last confirmed word, but never more than the window.
                    int rate = AudioDevices.SampleRate;
                    int fromConfirmed = Math.Max(0, (int)((_agreement.ConfirmedEnd.TotalSeconds - WindowMargin) * rate));
                    snapshotStart = Math.Max(fromConfirmed, _samples.Count - (int)(options.InterimWindowSeconds * rate));
                    if (_samples.Count - snapshotStart < MinimumWindowSeconds * rate)
                    {
                        // Too little audio for a useful result (whisper pads it and then invents words): wait for more.
                        _partialRunning = false;
                        _samplesAtPartial = previousMark;
                    }
                    else snapshot = _samples.GetRange(snapshotStart, _samples.Count - snapshotStart).ToArray();
                }
            }
        }
        Level?.Invoke(level);
        if (reason != StopReason.None) Finish(manual: false, reason, generation);
        else if (snapshot is not null)
        {
            int start = snapshotStart;
            _ = Task.Run(() => interim is null ? RunPartialAsync(generation, snapshot, token) : RunInterimAsync(generation, snapshot, start, token));
        }
    }

    private async Task RunPartialAsync(int generation, short[] snapshot, CancellationToken token)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            string text = await transcriber.TranscribeAsync(snapshot, options, token);
            lock (_lock)
            {
                if (generation != _generation || _state != DictationState.Recording) return;
                Metrics.Cycle(new(snapshot.Length / (double)AudioDevices.SampleRate, clock.Elapsed.TotalMilliseconds, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
            }
            Partial?.Invoke(text);
            Interim?.Invoke("", text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(generation, ex); }
        finally { lock (_lock) if (generation == _generation) _partialRunning = false; }
    }

    private async Task RunInterimAsync(int generation, short[] window, int startSample, CancellationToken token)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var relative = await interim!.TranscribeWordsAsync(window, options, token);
            var offset = TimeSpan.FromSeconds(startSample / (double)AudioDevices.SampleRate);
            string confirmed, tentative;
            lock (_lock)
            {
                if (generation != _generation || _state != DictationState.Recording) return;
                _agreement.Feed(relative.Select(w => new TimedWord(w.Text, w.Start + offset, w.End + offset)).ToList());
                confirmed = _agreement.ConfirmedText;
                tentative = _agreement.TentativeText;
                Metrics.Cycle(new(window.Length / (double)AudioDevices.SampleRate, clock.Elapsed.TotalMilliseconds, relative.Count));
            }
            Partial?.Invoke((confirmed + " " + tentative).Trim());
            Interim?.Invoke(confirmed, tentative);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(generation, ex); }
        finally { lock (_lock) if (generation == _generation) _partialRunning = false; }
    }

    private void Finish(bool manual, StopReason reason, int? expectedGeneration)
    {
        IAudioSource? source;
        short[] samples;
        bool speech;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_state != DictationState.Recording || (expectedGeneration is { } g && g != _generation)) return;
            _state = DictationState.Processing;
            generation = _generation;
            samples = _samples.ToArray();
            speech = _detector.SpeechHeard;
            source = _source;
            _source = null;
            token = _work.Token;
            Metrics.StopRequested(samples.Length / (double)AudioDevices.SampleRate);
        }
        StateChanged?.Invoke(DictationState.Processing);
        ReleaseSource(source);
        _ = Task.Run(async () => {
            string text = "";
            try
            {
                // Without any speech whisper tends to invent text, so an empty recording is not transcribed at all.
                bool transcribe = speech || (manual && samples.Length >= AudioDevices.SampleRate / 2);
                if (transcribe) text = await transcriber.TranscribeAsync(samples, options, token);
                lock (_lock)
                {
                    if (generation != _generation) return;
                    _state = DictationState.Idle;
                    Metrics.FinalArrived();
                }
                StateChanged?.Invoke(DictationState.Idle);
                Completed?.Invoke(new(text, reason, manual));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(generation, ex); }
        });
    }

    private void Fail(int generation, Exception ex)
    {
        IAudioSource? source;
        lock (_lock)
        {
            if (generation != _generation || _state == DictationState.Idle) return;
            _generation++;
            _work.Cancel();
            source = _source;
            _source = null;
            _state = DictationState.Idle;
        }
        ReleaseSource(source);
        StateChanged?.Invoke(DictationState.Idle);
        Failed?.Invoke(ex);
    }

    // Stopping from inside the audio callback can deadlock in the driver, so it happens elsewhere.
    private static void ReleaseSource(IAudioSource? source)
    {
        if (source is null) return;
        _ = Task.Run(() => { try { source.Dispose(); } catch (Exception) { } });
    }

    public void Dispose() => Cancel();
}
