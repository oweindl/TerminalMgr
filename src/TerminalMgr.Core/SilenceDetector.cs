namespace TerminalMgr.Core;

public enum StopReason { None, Silence, NoSpeech }

/// <summary>
/// Decides from the audio itself when a dictation ends: after <c>silenceSeconds</c> without speech once speech has been heard,
/// or after <c>initialSeconds</c> if nothing was said at all. Time is counted in samples, so tests can use synthetic audio.
/// </summary>
public sealed class SilenceDetector
{
    private const int WindowMilliseconds = 20;
    private const int SpeechWindowsNeeded = 3; // 60 ms of sound in a row; a single click is not speech
    private readonly int _windowSamples, _sampleRate;
    private readonly long _silenceSamples, _initialSamples;
    private readonly double _threshold;
    private readonly short[] _window;
    private int _filled, _speechRun;
    private long _total, _silent;

    public SilenceDetector(int sampleRate = 16000, double silenceSeconds = 1.5, double threshold = 0.015, double initialSeconds = 5)
    {
        _sampleRate = sampleRate;
        _windowSamples = sampleRate * WindowMilliseconds / 1000;
        _window = new short[_windowSamples];
        _silenceSamples = (long)(silenceSeconds * sampleRate);
        _initialSamples = (long)(initialSeconds * sampleRate);
        _threshold = Math.Clamp(threshold, 0.001, 0.5);
    }

    public bool SpeechHeard { get; private set; }
    public StopReason Reason { get; private set; }
    /// <summary>Loudness of the latest window scaled so that the threshold sits at one third (0..1) for a level meter.</summary>
    public double Level { get; private set; }
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)_total / _sampleRate);

    public StopReason Feed(ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
        {
            _window[_filled++] = sample;
            if (_filled < _windowSamples) continue;
            _filled = 0;
            Process();
        }
        return Reason;
    }

    private void Process()
    {
        double sum = 0;
        foreach (short s in _window) sum += (double)s * s;
        double rms = Math.Sqrt(sum / _windowSamples) / 32768.0;
        Level = Math.Clamp(rms / (_threshold * 3), 0, 1);
        _total += _windowSamples;
        if (rms >= _threshold)
        {
            if (++_speechRun >= SpeechWindowsNeeded) { SpeechHeard = true; _silent = 0; }
            else _silent += _windowSamples;
        }
        else { _speechRun = 0; _silent += _windowSamples; }
        if (Reason != StopReason.None) return;
        if (SpeechHeard && _silent >= _silenceSamples) Reason = StopReason.Silence;
        else if (!SpeechHeard && _total >= _initialSamples) Reason = StopReason.NoSpeech;
    }
}
