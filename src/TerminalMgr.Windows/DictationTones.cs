using NAudio.Wave;

namespace TerminalMgr.Windows;

/// <summary>
/// The short tones at the start and the end of a recording. They only exist in memory (generated sine tones with a soft attack and
/// release, so there is no click) and play on the default output device without blocking the caller.
/// The start tone rises (two notes), the end tone falls, so the two cannot be mixed up.
/// </summary>
public static class DictationTones
{
    public const int SampleRate = 44100;

    /// <summary>16-bit mono PCM of the tone. <paramref name="volumePercent"/> scales the amplitude (100 % is a little below full scale).</summary>
    public static byte[] Generate(bool start, int volumePercent)
    {
        double amplitude = 0.8 * Math.Clamp(volumePercent, 0, 100) / 100.0 * short.MaxValue;
        (double Hz, double Seconds)[] notes = start ? [(740, 0.06), (1108, 0.08)] : [(932, 0.06), (622, 0.09)];
        var samples = new List<short>();
        foreach (var (hz, seconds) in notes)
        {
            int count = (int)(seconds * SampleRate), ramp = Math.Max(1, (int)(0.008 * SampleRate));
            for (int i = 0; i < count; i++)
            {
                double envelope = Math.Min(1.0, Math.Min(i / (double)ramp, (count - 1 - i) / (double)ramp));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * amplitude * envelope));
            }
        }
        var bytes = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Plays the tone in the background. Never throws: no output device is no reason to disturb a dictation.</summary>
    public static void Play(bool start, int volumePercent)
    {
        if (volumePercent <= 0) return;
        try
        {
            var bytes = Generate(start, volumePercent);
            var output = new WaveOutEvent();
            var stream = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(SampleRate, 16, 1));
            output.PlaybackStopped += (_, _) => { output.Dispose(); stream.Dispose(); };
            output.Init(stream);
            output.Play();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException) { /* no sound device: stay silent */ }
    }
}
