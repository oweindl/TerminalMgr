namespace TerminalMgr.Core;

public sealed record WhisperModel(string Name, string FileName, long ApproxBytes, string Description)
{
    public Uri Url => new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName);
    public string SizeText => ApproxBytes >= 500_000_000 ?$"ca. {ApproxBytes / 1e9:0.0} GB" : $"ca. {ApproxBytes / 1_000_000} MB";
}

/// <summary>The ggml models offered for dictation. They live in %LOCALAPPDATA%\TerminalMgr\models, never in the install folder.</summary>
public static class WhisperModels
{
    public const string DefaultName = "large-v3-turbo-q5_0";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("large-v3-turbo-q5_0", "ggml-large-v3-turbo-q5_0.bin", 574_000_000, "Gutes Deutsch mit englischen Fachbegriffen (Vorgabe)"),
        new("small-q5_1", "ggml-small-q5_1.bin", 190_000_000, "Leichter, für schwächere Rechner"),
        new("small", "ggml-small.bin", 488_000_000, "Leichter, unquantisiert"),
        new("tiny", "ggml-tiny.bin", 78_000_000, "Sehr schnell, aber ungenau (zum Ausprobieren)")
    ];

    public static WhisperModel Get(string? name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string PathOf(WhisperModel model, string? modelsDirectory = null) =>
        Path.Combine(modelsDirectory ?? AppPaths.ModelsDirectory(), model.FileName);

    /// <summary>A model counts as present when its file exists and is not an obviously truncated download.</summary>
    public static bool IsInstalled(WhisperModel model, string? modelsDirectory = null)
    {
        var file = new FileInfo(PathOf(model, modelsDirectory));
        return file.Exists && file.Length > model.ApproxBytes / 2;
    }

    /// <summary>ggml model files start with the magic "lmgg" (little-endian 0x67676d6c).</summary>
    public static bool LooksLikeModel(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual("lmgg"u8);
        }
        catch (IOException) { return false; }
    }
}
