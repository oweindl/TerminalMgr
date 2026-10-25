using System.Diagnostics;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

public enum DictationState { Idle, Recording, Processing }

/// <param name="Text">Final recognized text; empty when nothing was said.</param>
/// <param name="Manual">True when the user ended the recording instead of the silence detector.</param>
public sealed record DictationResult(string Text, StopReason Reason, bool Manual);

/// <summary>The engine cannot work on this machine for a reason the user can act on (a Windows privacy setting, a missing service).</summary>
public sealed class SpeechUnavailableException(string message) : Exception(message);

/// <summary>
/// What every recognizer offers the dictation panel: start, stop, cancel, a state, an input level, a provisional text split into
/// confirmed and tentative words, and the final text. Events arrive on worker threads; the UI has to marshal them.
/// </summary>
public interface ISpeechEngine : IDisposable
{
    string Id { get; }
    DictationState State { get; }
    DictationMetrics Metrics { get; }
    event Action<DictationState>? StateChanged;
    event Action<double>? Level;
    /// <summary>Provisional text while speaking: (confirmed words, tentative words). Confirmed words do not change again.</summary>
    event Action<string, string>? Interim;
    event Action<DictationResult>? Completed;
    event Action<Exception>? Failed;
    void Start();
    /// <summary>Ends the recording now; the final text arrives through <see cref="Completed"/>.</summary>
    void Stop();
    /// <summary>Throws the recording away; no result is reported.</summary>
    void Cancel();
}

/// <param name="WindowSeconds">Length of the audio that was recognized in this cycle.</param>
/// <param name="Milliseconds">How long the recognition took.</param>
public sealed record InterimCycle(double WindowSeconds, double Milliseconds, int Words);

/// <summary>Timings of one dictation round, for the comparison of engines (measuring mode) and for the diagnostics log.</summary>
public sealed class DictationMetrics
{
    private readonly object _lock = new();
    private readonly List<InterimCycle> _cycles = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public List<InterimCycle> Cycles { get { lock (_lock) return [.. _cycles]; } }
    /// <summary>From the first speech sample (or the engine's start) to the first provisional text.</summary>
    public double? FirstInterimMilliseconds { get; private set; }
    /// <summary>From the end of the recording to the final text.</summary>
    public double? FinalMilliseconds { get; private set; }
    public double FinalAudioSeconds { get; private set; }
    private double? _speechAt, _stopAt;

    public void SpeechStarted() { lock (_lock) _speechAt ??= _clock.Elapsed.TotalMilliseconds; }
    public void Cycle(InterimCycle cycle)
    {
        lock (_lock)
        {
            _cycles.Add(cycle);
            if (cycle.Words > 0 && FirstInterimMilliseconds is null && _speechAt is { } at) FirstInterimMilliseconds = _clock.Elapsed.TotalMilliseconds - at;
        }
    }
    /// <summary>For engines that report their provisional text themselves.</summary>
    public void InterimArrived() { lock (_lock) if (FirstInterimMilliseconds is null && _speechAt is { } at) FirstInterimMilliseconds = _clock.Elapsed.TotalMilliseconds - at; }
    public void StopRequested(double audioSeconds) { lock (_lock) { _stopAt = _clock.Elapsed.TotalMilliseconds; FinalAudioSeconds = audioSeconds; } }
    public void FinalArrived() { lock (_lock) if (_stopAt is { } at) FinalMilliseconds = _clock.Elapsed.TotalMilliseconds - at; }

    public double AverageCycleMilliseconds { get { lock (_lock) return _cycles.Count == 0 ? 0 : _cycles.Average(c => c.Milliseconds); } }
    public double MaxCycleMilliseconds { get { lock (_lock) return _cycles.Count == 0 ? 0 : _cycles.Max(c => c.Milliseconds); } }

    public string Summary()
    {
        string first = FirstInterimMilliseconds is { } f ? $"{f:0} ms" : "–";
        string final = FinalMilliseconds is { } n ? $"{n:0} ms" : "–";
        return $"erstes Zwischenergebnis {first} · {Cycles.Count} Zyklen, Ø {AverageCycleMilliseconds:0} ms, max {MaxCycleMilliseconds:0} ms · Endtext {final} nach Ende ({FinalAudioSeconds:0.0} s Audio)";
    }
}
