using System.Reflection;

namespace TerminalMgr.App;

public static class AppInfo
{
    /// <summary>The version from Directory.Build.props, without a source-revision suffix ("0.2.0").</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        string info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "unbekannt";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
