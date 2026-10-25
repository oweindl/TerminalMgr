using System.Text.Json;

namespace TerminalMgr.Core;

public sealed class SettingsStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public AppSettings Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
            ?? throw new InvalidDataException("Einstellungen sind leer.")
        : new AppSettings();

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
