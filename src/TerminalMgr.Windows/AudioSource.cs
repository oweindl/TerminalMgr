using NAudio.Wave;

namespace TerminalMgr.Windows;

/// <summary>A stream of 16 kHz mono 16-bit samples. Audio only ever exists in memory.</summary>
public interface IAudioSource : IDisposable
{
    event Action<short[]>? Samples;
    event Action<Exception>? Failed;
    void Start();
    void Stop();
}

public static class AudioDevices
{
    public const int SampleRate = 16000;

    public static IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    /// <summary>Index of the named device; -1 selects the Windows default. Windows truncates product names to 31 characters.</summary>
    public static int IndexOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        var names = Names().ToList();
        int exact = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return exact >= 0 ? exact : names.FindIndex(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase) || n.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class MicrophoneSource : IAudioSource
{
    private readonly WaveInEvent _input;
    private bool _stopped;
    public event Action<short[]>? Samples;
    public event Action<Exception>? Failed;

    public MicrophoneSource(string? deviceName = null)
    {
        _input = new WaveInEvent {
            DeviceNumber = Math.Max(0, AudioDevices.IndexOf(deviceName)),
            WaveFormat = new WaveFormat(AudioDevices.SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        _input.DataAvailable += (_, e) => {
            if (_stopped || e.BytesRecorded < 2) return;
            var samples = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, samples.Length * 2);
            Samples?.Invoke(samples);
        };
        _input.RecordingStopped += (_, e) => { if (e.Exception is not null && !_stopped) Failed?.Invoke(e.Exception); };
    }

    public void Start()
    {
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("Es ist kein Mikrofon vorhanden.");
        _input.StartRecording();
    }

    public void Stop()
    {
        _stopped = true;
        try { _input.StopRecording(); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        Stop();
        _input.Dispose();
    }
}

/// <summary>
/// Plays prepared samples into the dictation as if they came from a microphone, in real time, followed by silence.
/// For the measuring mode and for tests; the audio never leaves memory.
/// </summary>
public sealed class WavFileSource(short[] samples, double trailingSilenceSeconds = 2.5, double speed = 1.0) : IAudioSource
{
    private const int ChunkMilliseconds = 100;
    private CancellationTokenSource? _cancel;
    public event Action<short[]>? Samples;
    public event Action<Exception>? Failed;

    public void Start()
    {
        var cancel = _cancel = new CancellationTokenSource();
        _ = Task.Factory.StartNew(() => Pump(cancel.Token), TaskCreationOptions.LongRunning);
    }

    private void Pump(CancellationToken cancel)
    {
        try
        {
            int chunk = AudioDevices.SampleRate * ChunkMilliseconds / 1000;
            int total = samples.Length + (int)(trailingSilenceSeconds * AudioDevices.SampleRate);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int offset = 0; offset < total && !cancel.IsCancellationRequested; offset += chunk)
            {
                var block = new short[chunk];
                int available = Math.Max(0, Math.Min(chunk, samples.Length - offset));
                if (available > 0) Array.Copy(samples, offset, block, 0, available);
                // A chunk is delivered when its last sample has been "recorded", like a real device would.
                double due = (offset + chunk) / (double)AudioDevices.SampleRate / speed * 1000;
                int wait = (int)(due - clock.Elapsed.TotalMilliseconds);
                if (wait > 0) cancel.WaitHandle.WaitOne(wait);
                if (cancel.IsCancellationRequested) return;
                Samples?.Invoke(block);
            }
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    public void Stop() => _cancel?.Cancel();
    public void Dispose() => Stop();
}
