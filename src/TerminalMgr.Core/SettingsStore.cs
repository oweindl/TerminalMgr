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

    private bool _backedUp;

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        BackUpOncePerRun();
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// Before the first write of a run the existing file is copied to settings.yyyyMMdd-HHmmss.bak (the last five are kept), so a
    /// wrong write, whatever its cause, never destroys the only copy of the servers.
    /// </summary>
    private void BackUpOncePerRun()
    {
        if (_backedUp) return;
        _backedUp = true;
        try
        {
            if (!File.Exists(_path)) return;
            File.Copy(_path, Path.Combine(directory, $"settings.{DateTime.Now:yyyyMMdd-HHmmss}.bak"), overwrite: true);
            foreach (var old in Directory.GetFiles(directory, "settings.*.bak").OrderByDescending(f => f).Skip(5)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A backup must never stop saving. */ }
    }
}
