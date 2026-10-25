namespace TerminalMgr.Core;

/// <summary>User data lives outside the installation folder so that updates never touch it.</summary>
public static class AppPaths
{
    /// <summary>TERMINALMGR_DATA_DIR / TERMINALMGR_INSTALL_DIR redirect the folders for tests and development, so that a test
    /// instance never touches the settings or the installation of the real app.</summary>
    public static string DataDirectory(string? localAppData = null) =>
        localAppData is null && Environment.GetEnvironmentVariable("TERMINALMGR_DATA_DIR") is { Length: > 0 } custom ? custom
        : Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalMgr");

    /// <summary>The fixed place of the installed app; the shortcuts point here and a self-update replaces only this folder.</summary>
    public static string InstallDirectory(string? localAppData = null) =>
        localAppData is null && Environment.GetEnvironmentVariable("TERMINALMGR_INSTALL_DIR") is { Length: > 0 } custom ? custom
        : Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TerminalMgr");

    public static string UpdateDirectory(string? localAppData = null) => Path.Combine(DataDirectory(localAppData), "update");

    public static string ModelsDirectory(string? localAppData = null) => Path.Combine(DataDirectory(localAppData), "models");

    public static bool IsInside(string path, string directory)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
