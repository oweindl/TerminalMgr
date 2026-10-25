using TerminalMgr.Core;

namespace TerminalMgr.Windows;

public enum DictationState { Idle, Recording, Processing }

/// <param name="Text">Final recognized text; empty when nothing was said.</param>
/// <param name="Manual">True when the user ended the recording instead of the silence detector.</param>
public sealed record DictationResult(string Text, StopReason Reason, bool Manual);

/// <summary>
/// One dictation round: record, show a provisional text while speaking (whisper does not stream, so the segment so far is
/// transcribed again every <c>partialSeconds</c> of audio), stop on silence, then transcribe the whole segment for the final text.
/// Events arrive on worker threads; the UI has to marshal them.
/// </summary>
public sealed class DictationSession(Func<IAudioSource> createSource, ITranscriber transcriber, DictationOptions options, double partialSeconds = 1.75) : IDisposable
{
    private readonly object _lock = new();
    private readonly List<short> _samples = [];
    private IAudioSource? _source;
    private SilenceDetector _detector = new();
    private CancellationTokenSource _work = new();
    private DictationState _state;
    private int _generation, _samplesAtPartial;
    private bool _partialRunning;

    public event Action<DictationState>? StateChanged;
    public event Action<double>? Level;
    public event Action<string>? Partial;
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
            _samplesAtPartial = 0;
            _partialRunning = false;
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
        CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation || _state != DictationState.Recording) return;
            _samples.AddRange(chunk);
            reason = _detector.Feed(chunk);
            level = _detector.Level;
            token = _work.Token;
            if (reason == StopReason.None && _detector.SpeechHeard && !_partialRunning &&
                _samples.Count - _samplesAtPartial >= partialSeconds * AudioDevices.SampleRate)
            {
                _partialRunning = true;
                _samplesAtPartial = _samples.Count;
                snapshot = _samples.ToArray();
            }
        }
        Level?.Invoke(level);
        if (reason != StopReason.None) Finish(manual: false, reason, generation);
        else if (snapshot is not null) _ = Task.Run(() => RunPartialAsync(generation, snapshot, token));
    }

    private async Task RunPartialAsync(int generation, short[] snapshot, CancellationToken token)
    {
        try
        {
            string text = await transcriber.TranscribeAsync(snapshot, options, token);
            lock (_lock) if (generation != _generation || _state != DictationState.Recording) return;
            Partial?.Invoke(text);
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
