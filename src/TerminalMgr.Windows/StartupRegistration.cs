using Microsoft.Win32;

namespace TerminalMgr.Windows;

/// <summary>
/// "Start with Windows": a value under HKCU\...\Run, which needs no administrator rights and which Windows lists under
/// Settings > Apps > Startup. The key is injectable so tests never touch the real autostart list.
/// </summary>
public sealed class StartupRegistration(string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "TerminalMgr")
{
    /// <summary>The path is quoted so that spaces in it cannot turn into arguments.</summary>
    public static string CommandFor(string exePath) => "\"" + exePath.Replace("\"", "") + "\"";

    public string? CurrentCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }

    public bool IsEnabled(string exePath) => string.Equals(CurrentCommand(), CommandFor(exePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Brings the registry in line with the setting. Returns true when something was changed.</summary>
    public bool Apply(bool enabled, string exePath)
    {
        if (enabled)
        {
            if (IsEnabled(exePath)) return false;
            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            key.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
            return true;
        }
        if (CurrentCommand() is null) return false;
        using var existing = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        existing?.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }
}
