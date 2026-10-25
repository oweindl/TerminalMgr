using System.Diagnostics;
using System.Text;
using TerminalMgr.Core;
using TerminalMgr.Windows;

/// <summary>A point of the provisional text over time.</summary>
public sealed record BenchPoint(double Milliseconds, string Confirmed, string Tentative);

public sealed record BenchResult(string Engine, string Name, string Reference, string FinalText, double AudioSeconds, double FinalWer, double InterimWer,
    double? FirstInterimMs, double? FinalMs, double AverageCycleMs, double MaxCycleMs, int Cycles, int ConfirmedChanges, IReadOnlyList<BenchPoint> Timeline, string? Error)
{
    public string Line() => Error is not null
        ? $"| {Engine} | {Name} | {AudioSeconds:0.0} s | nicht messbar: {Error} | | | | | | |"
        : $"| {Engine} | {Name} | {AudioSeconds:0.0} s | {(FirstInterimMs is { } f ? $"{f:0} ms" : "–")} | {Cycles} | {AverageCycleMs:0} / {MaxCycleMs:0} ms | {(FinalMs is { } n ? $"{n:0} ms" : "–")} | {InterimWer:P0} | {FinalWer:P0} | {ConfirmedChanges} |";
}

/// <summary>
/// The measuring mode: runs one engine over prepared speech and records when the provisional text appears, how long a cycle takes,
/// how long the final text needs after the end of the speech, how far off the texts are, and whether confirmed words ever changed.
/// </summary>
public static class SpeechBench
{
    public static string TableHeader =>
        "| Engine | Probe | Audio | erstes Zwischenergebnis | Zyklen | Zyklus Ø / max | Endtext nach Ende | WER Zwischenstand | WER Endtext | geänderte bestätigte Wörter |" + Environment.NewLine +
        "|---|---|---|---|---|---|---|---|---|---|";

    public static async Task<BenchResult> RunAsync(string engineName, string name, ISpeechEngine engine, string reference, double audioSeconds, TimeSpan timeout, Action? whileRecording = null)
    {
        var timeline = new List<BenchPoint>();
        var clock = Stopwatch.StartNew();
        string lastConfirmed = "", lastFull = "";
        int changes = 0;
        var done = new TaskCompletionSource<DictationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Interim += (confirmed, tentative) =>
        {
            lock (timeline)
            {
                // Confirmed words must only ever grow at the end.
                if (lastConfirmed.Length > 0 && !confirmed.StartsWith(lastConfirmed, StringComparison.Ordinal)) changes++;
                lastConfirmed = confirmed;
                lastFull = (confirmed + " " + tentative).Trim();
                timeline.Add(new(clock.Elapsed.TotalMilliseconds, confirmed, tentative));
            }
        };
        if (Environment.GetEnvironmentVariable("TERMINALMGR_BENCH_TRACE") == "1")
        {
            engine.StateChanged += state => Console.WriteLine($"    [{clock.ElapsedMilliseconds} ms] Zustand {state}");
            engine.Interim += (confirmed, tentative) => Console.WriteLine($"    [{clock.ElapsedMilliseconds} ms] zwischen: {confirmed} | {tentative}");
            engine.Failed += ex => Console.WriteLine($"    [{clock.ElapsedMilliseconds} ms] Fehler: {ex}");
        }
        engine.Completed += result => done.TrySetResult(result);
        engine.Failed += ex => done.TrySetException(ex);
        try
        {
            engine.Start();
            whileRecording?.Invoke();
            var result = await done.Task.WaitAsync(timeout);
            var m = engine.Metrics;
            return new(engineName, name, reference, result.Text, audioSeconds, WordErrorRate.Compute(reference, result.Text), WordErrorRate.Compute(reference, lastFull),
                m.FirstInterimMilliseconds, m.FinalMilliseconds, m.AverageCycleMilliseconds, m.MaxCycleMilliseconds, m.Cycles.Count, changes, timeline, null);
        }
        catch (Exception ex)
        {
            engine.Cancel();
            return new(engineName, name, reference, "", audioSeconds, 1, 1, null, null, 0, 0, 0, 0, timeline, ex is TimeoutException ? "Zeitüberschreitung" : ex.Message);
        }
    }

    /// <summary>Speech from the Windows voice, 16 kHz mono; empty when no voice of the culture is installed.</summary>
    public static short[] Synthesize(string culture, string sentence)
    {
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Culture.Name.StartsWith(culture));
        if (voice is null) return [];
        synth.SelectVoice(voice.VoiceInfo.Name);
        using var wav = new MemoryStream();
        synth.SetOutputToWaveStream(wav);
        synth.Speak(sentence);
        wav.Position = 0;
        using var reader = new NAudio.Wave.WaveFileReader(wav);
        using var resampled = new NAudio.Wave.MediaFoundationResampler(new NAudio.Wave.WaveFormatConversionStream(new NAudio.Wave.WaveFormat(reader.WaveFormat.SampleRate, 16, 1), reader), new NAudio.Wave.WaveFormat(16000, 16, 1));
        var pcm = new MemoryStream();
        var buffer = new byte[16000];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, read);
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm.ToArray(), 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>Several sentences with a short pause (shorter than the silence that ends a recording) between them.</summary>
    public static short[] SynthesizeSentences(string culture, IEnumerable<string> sentences, double pauseSeconds = 0.6)
    {
        var all = new List<short>();
        foreach (string sentence in sentences)
        {
            if (all.Count > 0) all.AddRange(new short[(int)(pauseSeconds * AudioDevices.SampleRate)]);
            all.AddRange(TrimSilence(Synthesize(culture, sentence)));
        }
        return all.ToArray();
    }

    /// <summary>The synthesizer pads each sentence with up to a second of silence; a real speaker would not pause that long mid-thought.</summary>
    public static short[] TrimSilence(short[] samples, double threshold = 0.01, double marginSeconds = 0.1)
    {
        int rate = AudioDevices.SampleRate, margin = (int)(marginSeconds * rate), limit = (int)(threshold * short.MaxValue);
        int first = Array.FindIndex(samples, s => Math.Abs((int)s) > limit);
        int last = Array.FindLastIndex(samples, s => Math.Abs((int)s) > limit);
        if (first < 0) return samples;
        int from = Math.Max(0, first - margin), to = Math.Min(samples.Length, last + margin);
        return samples[from..to];
    }

    public static string Describe(BenchResult r)
    {
        var text = new StringBuilder();
        text.AppendLine($"  Referenz: {r.Reference}");
        text.AppendLine($"  Endtext:  {r.FinalText}");
        if (r.Timeline.Count > 0) text.AppendLine($"  Zwischenstand zuletzt: {(r.Timeline[^1].Confirmed + " " + r.Timeline[^1].Tentative).Trim()}");
        return text.ToString().TrimEnd();
    }
}

/// <summary>Prints every recognition call (window length, duration, result) when TERMINALMGR_BENCH_TRACE=1.</summary>
public sealed class TracingTranscriber(WhisperTranscriber inner, string label) : IWordTranscriber, ITranscriber
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public async Task<IReadOnlyList<TimedWord>> TranscribeWordsAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        double start = _clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"      {label} Worte: Start {start:0} ms, Fenster {samples.Length / 16000.0:0.0} s");
        try
        {
            var words = await inner.TranscribeWordsAsync(samples, options, cancellationToken);
            Console.WriteLine($"      {label} Worte: fertig nach {_clock.Elapsed.TotalMilliseconds - start:0} ms: {string.Join(" ", words.Select(w => w.Text))}");
            return words;
        }
        catch (Exception ex) { Console.WriteLine($"      {label} Worte: {ex.GetType().Name} {ex.Message}"); throw; }
    }

    public async Task<string> TranscribeAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        double start = _clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"      {label} Text: Start {start:0} ms, Audio {samples.Length / 16000.0:0.0} s");
        try
        {
            string text = await inner.TranscribeAsync(samples, options, cancellationToken);
            Console.WriteLine($"      {label} Text: fertig nach {_clock.Elapsed.TotalMilliseconds - start:0} ms: {text}");
            return text;
        }
        catch (Exception ex) { Console.WriteLine($"      {label} Text: {ex.GetType().Name} {ex.Message}"); throw; }
    }
}
