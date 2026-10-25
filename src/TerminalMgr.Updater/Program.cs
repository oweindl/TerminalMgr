using System.Diagnostics;
using System.Windows.Forms;
using TerminalMgr.Core;

namespace TerminalMgr.Updater;

/// <summary>
/// Runs from a copy in the temp folder after TerminalMgr has quit: waits for the app to end, swaps the installation folder for
/// the staged, verified new version (keeping a backup), starts the new app and checks that it comes up. If it does not,
/// the old version is restored and started again. Arguments: --pid N --install DIR --staged DIR --exe NAME [--cleanup DIR] [--log FILE]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2) if (args[i].StartsWith("--")) options[args[i].Substring(2)] = args[i + 1];
        if (!options.TryGetValue("install", out var install) || !options.TryGetValue("staged", out var staged) || !options.TryGetValue("exe", out var exeName))
            return 64; // usage
        options.TryGetValue("log", out var logPath);
        options.TryGetValue("cleanup", out var cleanup);

        void Log(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Log("Updater gestartet: " + install + " <- " + staged);
        if (options.TryGetValue("pid", out var pidText) && int.TryParse(pidText, out int pid))
        {
            try
            {
                using var app = Process.GetProcessById(pid);
                Log("Warte auf das Ende von TerminalMgr (PID " + pid + ") ...");
                if (!app.WaitForExit(60_000)) { Log("TerminalMgr hat sich nicht beendet; Abbruch."); Show("TerminalMgr hat sich nicht beendet. Die Aktualisierung wurde abgebrochen."); return 2; }
            }
            catch (ArgumentException) { /* already gone */ }
        }

        string exe = Path.Combine(install, exeName);
        var result = UpdateSwap.Apply(install, staged, () => StartAndVerify(exe, Log), () => Start(exe), Log);
        Log(result.Message);
        if (result.Success)
        {
            if (!string.IsNullOrEmpty(cleanup)) try { Directory.Delete(cleanup, true); } catch (Exception ex) { Log("Aufräumen fehlgeschlagen: " + ex.Message); }
            return 0;
        }
        Show("Die Aktualisierung von TerminalMgr ist fehlgeschlagen.\n\n" + result.Message + "\n\nProtokoll: " + logPath);
        return 1;
    }

    private static void Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });

    /// <summary>The new app counts as started when its window shows up and it is still running a few seconds later.</summary>
    private static bool StartAndVerify(string exe, Action<string> log)
    {
        using var process = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
        if (process is null) return false;
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) { log("Die neue Version hat sich sofort beendet (Exit " + process.ExitCode + ")."); return false; }
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) break;
            Thread.Sleep(250);
        }
        if (process.MainWindowHandle == IntPtr.Zero) { log("Kein Fenster der neuen Version innerhalb von 40 s."); try { process.Kill(); } catch (InvalidOperationException) { } return false; }
        Thread.Sleep(3000);
        if (process.HasExited) { log("Die neue Version hat sich kurz nach dem Start beendet (Exit " + process.ExitCode + ")."); return false; }
        log("Die neue Version läuft.");
        return true;
    }

    private static void Show(string text) => MessageBox.Show(text, "TerminalMgr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
