namespace TerminalMgr.Core;

/// <summary>User data lives outside the installation folder so that updates never touch it.</summary>
public static class AppPaths
{
    public static string DataDirectory(string? localAppData = null) => Path.Combine(
        localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalMgr");

    public static string ModelsDirectory(string? localAppData = null) => Path.Combine(DataDirectory(localAppData), "models");

    public static bool IsInside(string path, string directory)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
