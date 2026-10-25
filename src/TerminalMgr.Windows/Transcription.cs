using System.Text;
using TerminalMgr.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TerminalMgr.Windows;

public interface ITranscriber
{
    /// <summary>Recognized text for 16 kHz mono samples. Called off the UI thread.</summary>
    Task<string> TranscribeAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken);
}

public interface IWordTranscriber
{
    /// <summary>Recognized words with their times relative to the start of <paramref name="samples"/>. Called off the UI thread.</summary>
    Task<IReadOnlyList<TimedWord>> TranscribeWordsAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Local, offline speech recognition with whisper.cpp through Whisper.net. GPU (Vulkan) when available, else CPU.
/// One instance serves one model: the final text uses <see cref="DictationOptions.Model"/>, an <paramref name="interim"/> instance
/// the small <see cref="DictationOptions.InterimModel"/> for the provisional text.
/// </summary>
public sealed class WhisperTranscriber(string? modelsDirectory = null, bool interim = false) : ITranscriber, IWordTranscriber, IDisposable
{
    private const int MinimumSamples = AudioDevices.SampleRate * 12 / 10; // whisper.cpp rejects input shorter than 1 s
    private const int ContextPerSecond = 50;  // one encoder position per 20 ms of audio; 1500 positions cover the usual 30 s
    // One recognition at a time in the whole process: two whisper.cpp contexts running at once on the same GPU (the small model for the
    // provisional text and the large one for the final text) hung and crashed the Vulkan backend.
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;

    /// <summary>Which native runtime was loaded ("Vulkan", "Cpu", ...); empty before the first transcription.</summary>
    public string Backend => RuntimeOptions.LoadedLibrary?.ToString() ?? "";

    private string ModelPath(DictationOptions options) =>
        WhisperModels.PathOf(WhisperModels.Get(interim ? options.InterimModel : options.Model), modelsDirectory);

    /// <summary>Loads the model and runs one short recognition, so that the first real request does not pay for loading and shader compilation.</summary>
    public async Task PrepareAsync(DictationOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ModelPath(options))) return;
        await TranscribeWordsAsync(new short[MinimumSamples], options, cancellationToken);
    }

    public async Task<string> TranscribeAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        string path = ModelPath(options);
        if (!File.Exists(path)) throw new FileNotFoundException("Das Spracherkennungsmodell fehlt: " + Path.GetFileName(path), path);
        var audio = ToFloats(samples);
        await NativeGate.WaitAsync(cancellationToken);
        try
        {
            var builder = Factory(path).CreateBuilder().WithLanguage(options.Language).WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
            if (!string.IsNullOrWhiteSpace(options.InitialPrompt)) builder = builder.WithPrompt(options.InitialPrompt);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, CancellationToken.None)) { cancellationToken.ThrowIfCancellationRequested(); text.Append(segment.Text).Append(' '); }
            return DictationText.Clean(text.ToString());
        }
        finally { NativeGate.Release(); }
    }

    /// <summary>
    /// One word per segment (maximum segment length 1, split on words, token timestamps) and a reduced audio context: the encoder only
    /// looks at as many positions as the window is long instead of the usual 30 s, which is what makes a cycle fast.
    /// </summary>
    public async Task<IReadOnlyList<TimedWord>> TranscribeWordsAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        string path = ModelPath(options);
        if (!File.Exists(path)) throw new FileNotFoundException("Das Spracherkennungsmodell fehlt: " + Path.GetFileName(path), path);
        var audio = ToFloats(samples);
        int context = AudioContextFor(audio.Length);
        await NativeGate.WaitAsync(cancellationToken);
        try
        {
            var builder = Factory(path).CreateBuilder().WithLanguage(options.Language).WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .WithTokenTimestamps().WithMaxSegmentLength(1).SplitOnWord().WithAudioContextSize(context).WithNoContext()
                .WithTemperature(0f).WithTemperatureInc(0f); // no repeated decoding at higher temperatures: a provisional text has to be quick
            if (!string.IsNullOrWhiteSpace(options.InitialPrompt)) builder = builder.WithPrompt(options.InitialPrompt);
            await using var processor = builder.Build();
            var words = new List<TimedWord>();
            await foreach (var segment in processor.ProcessAsync(audio, CancellationToken.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = DictationText.Clean(segment.Text);
                if (text.Length > 0) words.Add(new(text, segment.Start, segment.End));
            }
            return words;
        }
        finally { NativeGate.Release(); }
    }

    // A cancelable token is not handed to Whisper.net: its native abort callback crashed the process (access violation inside whisper_full)
    // when a dictation round ran with a real token. Cancellation is honored between segments instead; a running recognition
    // finishes (well under a second for the small model) and its result is dropped.
    /// <summary>One encoder position per 20 ms plus some slack, rounded up to a multiple of 64 and never below 256 (odd small sizes made the GPU backend hang).</summary>
    public static int AudioContextFor(int sampleCount)
    {
        int positions = (int)Math.Ceiling(sampleCount / (double)AudioDevices.SampleRate * ContextPerSecond) + 64;
        return Math.Min(1500, Math.Max(256, (positions + 63) / 64 * 64));
    }

    private WhisperFactory Factory(string path)
    {
        if (_factory is null || _loadedPath != path)
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(path);
            _loadedPath = path;
        }
        return _factory;
    }

    private static float[] ToFloats(short[] samples)
    {
        var audio = new float[Math.Max(samples.Length, MinimumSamples)];
        for (int i = 0; i < samples.Length; i++) audio[i] = samples[i] / 32768f;
        return audio;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}

public static class ModelDownloader
{
    /// <summary>
    /// Downloads a model into <paramref name="directory"/> through a temporary file, so a canceled or broken download never
    /// looks like an installed model. Throws when the response is not a ggml file (for example an HTML error page).
    /// </summary>
    public static async Task DownloadAsync(WhisperModel model, string directory, IProgress<(long Done, long Total)>? progress,
        CancellationToken cancellationToken, Uri? source = null, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(directory);
        string target = WhisperModels.PathOf(model, directory), temp = target + ".part";
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        try
        {
            using var response = await client.GetAsync(source ?? model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? model.ApproxBytes;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    progress?.Report((done, total));
                }
            }
            if (!WhisperModels.LooksLikeModel(temp)) throw new InvalidDataException("Die heruntergeladene Datei ist kein Whisper-Modell.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }
}
