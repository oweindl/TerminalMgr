using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalMgr.Core;

/// <param name="Aliases">The names after "Host" in the SSH config block.</param>
/// <param name="Text">The block as written: the Host line and everything up to the next Host or Match line.</param>
public sealed record SshHostBlock(IReadOnlyList<string> Aliases, string Text);

/// <summary>Dictation settings as exported: the model and the microphone stay local (they depend on the machine).</summary>
public sealed record ExportedDictation
{
    public string Shortcut { get; init; } = "Ctrl+Y";
    public string Language { get; init; } = "de";
    public string InitialPrompt { get; init; } = "";
    public double SilenceSeconds { get; init; } = 1.5;
    public double LevelThreshold { get; init; } = 0.015;
}

/// <summary>App settings as exported. Not part of it: start with Windows, the skipped update version, the model, the microphone.</summary>
public sealed record ExportedSettings
{
    public int FontSize { get; init; } = 14;
    public int ClipHistoryLimit { get; init; } = 100;
    public bool AcceptOsc52 { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public ShortcutOptions Shortcuts { get; init; } = new();
    public ExportedDictation Dictation { get; init; } = new();
    /// <summary>Favorite tmux windows; they refer to servers by profile ID, which an imported profile keeps.</summary>
    public List<Favorite> Favorites { get; init; } = [];
}

public sealed record ConfigContent
{
    public List<Profile>? Profiles { get; init; }
    public ExportedSettings? Settings { get; init; }
    public List<ClipboardEntry>? PinnedClips { get; init; }
    public List<SshHostBlock>? SshHosts { get; init; }
}

public sealed record ExportSelection(bool Profiles, bool Settings, bool PinnedClips, bool SshHosts);

public sealed record ConfigFileInfo(int FormatVersion, string AppVersion, DateTimeOffset Exported, bool Encrypted);

public sealed class ConfigFormatException(string message) : Exception(message);
public sealed class ConfigPasswordException(string message) : Exception(message);

/// <summary>
/// The .tmconfig file: JSON with a format version. Optionally the content is encrypted with a password (AES-256-GCM, key from
/// PBKDF2-SHA256). Private keys, known_hosts, passwords and the speech model are never part of it.
/// </summary>
public static class ConfigFile
{
    public const int CurrentFormatVersion = 1;
    public const int KdfIterations = 210_000;
    public const long MaxFileBytes = 20_000_000;
    private const string FormatName = "tmconfig";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("tmconfig/1");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Envelope
    {
        [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("exported")] public DateTimeOffset Exported { get; set; }
        [JsonPropertyName("encryption")] public EncryptionInfo? Encryption { get; set; }
        [JsonPropertyName("content")] public ConfigContent? Content { get; set; }
        [JsonPropertyName("data")] public string? Data { get; set; }
    }

    private sealed class EncryptionInfo
    {
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "AES-256-GCM";
        [JsonPropertyName("kdf")] public string Kdf { get; set; } = "PBKDF2-SHA256";
        [JsonPropertyName("iterations")] public int Iterations { get; set; }
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    }

    /// <summary>Picks the chosen parts of the settings; <paramref name="hostBlocks"/> are the SSH config blocks of the exported profiles.</summary>
    public static ConfigContent Build(AppSettings settings, ExportSelection selection, IReadOnlyList<SshHostBlock> hostBlocks)
    {
        var dictation = settings.Dictation.Normalized();
        return new ConfigContent
        {
            Profiles = selection.Profiles ? settings.Profiles.ToList() : null,
            Settings = selection.Settings ? new ExportedSettings
            {
                FontSize = settings.FontSize, ClipHistoryLimit = settings.ClipHistoryLimit, AcceptOsc52 = settings.AcceptOsc52,
                CheckForUpdates = settings.Update.CheckOnStart, Shortcuts = settings.Shortcuts.Normalized(), Favorites = settings.Favorites.ToList(),
                Dictation = new ExportedDictation { Shortcut = dictation.Shortcut, Language = dictation.Language, InitialPrompt = dictation.InitialPrompt,
                    SilenceSeconds = dictation.SilenceSeconds, LevelThreshold = dictation.LevelThreshold }
            } : null,
            PinnedClips = selection.PinnedClips ? settings.PinnedClips.Where(c => c.Pinned).ToList() : null,
            SshHosts = selection.SshHosts ? hostBlocks.ToList() : null
        };
    }

    public static byte[] Serialize(ConfigContent content, string appVersion, DateTimeOffset now, string? password)
    {
        var envelope = new Envelope { FormatVersion = CurrentFormatVersion, AppVersion = appVersion, Exported = now };
        if (string.IsNullOrEmpty(password)) envelope.Content = content;
        else
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(content, Options);
            byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[plain.Length], tag = new byte[16];
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
            using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
            envelope.Encryption = new EncryptionInfo { Iterations = KdfIterations, Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag) };
            envelope.Data = Convert.ToBase64String(cipher);
        }
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <summary>Reads only the header: whether a password is needed and whether this app version understands the format.</summary>
    public static ConfigFileInfo Inspect(byte[] file) => ReadHeader(file, out _);

    public static (ConfigFileInfo Info, ConfigContent Content) Parse(byte[] file, string? password)
    {
        var info = ReadHeader(file, out var envelope);
        if (!info.Encrypted) return (info, envelope.Content ?? new ConfigContent());
        if (string.IsNullOrEmpty(password)) throw new ConfigPasswordException("Die Datei ist passwortgeschützt. Bitte das Passwort eingeben.");
        var e = envelope.Encryption!;
        try
        {
            byte[] salt = Convert.FromBase64String(e.Salt), nonce = Convert.FromBase64String(e.Nonce), tag = Convert.FromBase64String(e.Tag), cipher = Convert.FromBase64String(envelope.Data ?? "");
            if (e.Iterations is < 10_000 or > 5_000_000 || salt.Length != 16 || nonce.Length != 12 || tag.Length != 16) throw new ConfigFormatException("Die Verschlüsselungsangaben der Datei sind ungültig.");
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, e.Iterations, HashAlgorithmName.SHA256, 32), plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, tag.Length)) aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
            return (info, JsonSerializer.Deserialize<ConfigContent>(plain, Options) ?? new ConfigContent());
        }
        catch (CryptographicException) { throw new ConfigPasswordException("Falsches Passwort oder beschädigte Datei."); }
        catch (FormatException) { throw new ConfigFormatException("Die Datei ist beschädigt (ungültige Kodierung)."); }
        catch (JsonException) { throw new ConfigFormatException("Die Datei ist beschädigt."); }
    }

    private static ConfigFileInfo ReadHeader(byte[] file, out Envelope envelope)
    {
        if (file.Length > MaxFileBytes) throw new ConfigFormatException("Die Datei ist ungewöhnlich groß und wird nicht gelesen.");
        try { envelope = JsonSerializer.Deserialize<Envelope>(file, Options) ?? throw new ConfigFormatException("Die Datei ist leer."); }
        catch (JsonException) { throw new ConfigFormatException("Das ist keine TerminalMgr-Konfigurationsdatei."); }
        if (envelope.Format != FormatName) throw new ConfigFormatException("Das ist keine TerminalMgr-Konfigurationsdatei.");
        if (envelope.FormatVersion > CurrentFormatVersion)
            throw new ConfigFormatException($"Die Datei stammt aus einer neueren Version von TerminalMgr (Format {envelope.FormatVersion}, diese Version kennt Format {CurrentFormatVersion}). Bitte TerminalMgr aktualisieren und es noch einmal versuchen.");
        if (envelope.FormatVersion < 1) throw new ConfigFormatException("Die Formatversion der Datei ist ungültig.");
        bool encrypted = envelope.Encryption is not null;
        if (encrypted && string.IsNullOrEmpty(envelope.Data)) throw new ConfigFormatException("Die verschlüsselten Daten fehlen.");
        return new(envelope.FormatVersion, envelope.AppVersion, envelope.Exported, encrypted);
    }
}
