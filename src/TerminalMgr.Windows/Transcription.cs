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

/// <summary>Local, offline speech recognition with whisper.cpp through Whisper.net. GPU (Vulkan) when available, else CPU.</summary>
public sealed class WhisperTranscriber(string? modelsDirectory = null) : ITranscriber, IDisposable
{
    private const int MinimumSamples = AudioDevices.SampleRate * 12 / 10; // whisper.cpp rejects input shorter than 1 s
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;

    /// <summary>Which native runtime was loaded ("Vulkan", "Cpu", ...); empty before the first transcription.</summary>
    public string Backend => RuntimeOptions.LoadedLibrary?.ToString() ?? "";

    public async Task<string> TranscribeAsync(short[] samples, DictationOptions options, CancellationToken cancellationToken)
    {
        var model = WhisperModels.Get(options.Model);
        string path = WhisperModels.PathOf(model, modelsDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException("Das Spracherkennungsmodell fehlt: " + model.Name, path);
        var audio = new float[Math.Max(samples.Length, MinimumSamples)];
        for (int i = 0; i < samples.Length; i++) audio[i] = samples[i] / 32768f;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_factory is null || _loadedPath != path)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(path);
                _loadedPath = path;
            }
            var builder = _factory.CreateBuilder().WithLanguage(options.Language).WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
            if (!string.IsNullOrWhiteSpace(options.InitialPrompt)) builder = builder.WithPrompt(options.InitialPrompt);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken)) text.Append(segment.Text).Append(' ');
            return DictationText.Clean(text.ToString());
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
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
