using System.Diagnostics;
using System.IO;
using System.Windows;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Update check at start (before the automatic connections), the offer, and the hand-over to the separate updater.</summary>
public partial class MainWindow
{
    private Task<UpdateCheckResult>? _startupUpdateCheck;
    private bool _updating;

    private static Version CurrentVersion => UpdateInfo.TryParseVersion(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>Starts the look for a newer release in the background, so that it is usually done by the time the terminal is ready.</summary>
    private void BeginStartupUpdateCheck()
    {
        if (!_settings.Update.CheckOnStart) return;
        try { UpdateService.CleanUp(AppPaths.UpdateDirectory()); } catch (Exception) { /* leftovers of an earlier update are harmless */ }
        var service = UpdateService.FromEnvironment(AppInfo.Version);
        _startupUpdateCheck = Task.Run(async () => { try { return await service.CheckAsync(TimeSpan.FromSeconds(5), _shutdown.Token); } finally { service.Dispose(); } });
    }

    /// <summary>Awaited before the automatic connections start, so that a waiting offer does not tear down fresh sessions.</summary>
    private async Task HandleStartupUpdateAsync()
    {
        if (_startupUpdateCheck is not { } check) return;
        try { OfferUpdate(await check, manual: false); }
        catch (OperationCanceledException) when (_closing) { }
    }

    /// <summary>"Jetzt nach Updates suchen" in the settings: a fresh check, always with a visible answer.</summary>
    private async Task CheckForUpdatesNowAsync()
    {
        StatusText.Text = "Suche nach Updates …";
        using var service = UpdateService.FromEnvironment(AppInfo.Version);
        OfferUpdate(await service.CheckAsync(TimeSpan.FromSeconds(8), _shutdown.Token), manual: true);
    }

    private void OfferUpdate(UpdateCheckResult result, bool manual)
    {
        if (_updating || _closing) return;
        if (result.Release is not { } release)
        {
            // At start only the status line speaks (a private repository or no network is no reason to interrupt).
            if (result.Error is not null) StatusText.Text = result.Error;
            if (manual) MessageBox.Show(this, result.Error ?? "Es gibt keine neuere Version.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!UpdateInfo.ShouldOffer(CurrentVersion, release, manual ? null : _settings.Update.SkippedVersion))
        {
            if (manual) MessageBox.Show(this, $"TerminalMgr {AppInfo.Version} ist aktuell.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string installDirectory = AppPaths.InstallDirectory();
        bool canInstall = UpdateInfo.IsInstalledCopy(Environment.ProcessPath, installDirectory);
        if (canInstall && _sessions.Count > 0 &&
            MessageBox.Show(this, "Beim Aktualisieren werden die offenen SSH-Verbindungen getrennt. Fortfahren?", "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        using var service = UpdateService.FromEnvironment(AppInfo.Version);
        var dialog = new UpdateDialog(release, CurrentVersion, canInstall, installDirectory,
            (progress, cancel) => service.StageAsync(release, AppPaths.UpdateDirectory(), progress, cancel)) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case UpdateChoice.Skip:
                _settings = _settings with { Update = _settings.Update with { SkippedVersion = release.Version.ToString() } };
                SaveSettings();
                StatusText.Text = $"Version {release.Version} wird nicht mehr angeboten (Einstellungen: Übersprungene Version zurücksetzen).";
                break;
            case UpdateChoice.Installed when dialog.StagedApp is { } staged:
                LaunchUpdater(staged, installDirectory);
                break;
        }
    }

    /// <summary>
    /// The updater is copied to the temp folder first: it replaces the installation folder, which it could not do while running
    /// from inside it. Then this app quits; the updater waits for that, swaps the folders and starts the new version.
    /// </summary>
    private void LaunchUpdater(string stagedApp, string installDirectory)
    {
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "TerminalMgr.Updater.exe");
            if (!File.Exists(source)) throw new FileNotFoundException("TerminalMgr.Updater.exe fehlt im Programmordner.", source);
            string temp = Path.Combine(Path.GetTempPath(), "TerminalMgr-updater-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            foreach (string file in new[] { "TerminalMgr.Updater.exe", "TerminalMgr.Updater.exe.config" })
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, file))) File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(temp, file));
            string log = Path.Combine(AppPaths.UpdateDirectory(), "updater.log");
            var start = new ProcessStartInfo(Path.Combine(temp, "TerminalMgr.Updater.exe")) { UseShellExecute = false, WorkingDirectory = temp };
            foreach (string argument in new[] { "--pid", Environment.ProcessId.ToString(), "--install", installDirectory, "--staged", stagedApp,
                "--exe", "TerminalMgr.App.exe", "--cleanup", Directory.GetParent(stagedApp)!.FullName, "--log", log })
                start.ArgumentList.Add(argument);
            Process.Start(start);
            _updating = true;
            StatusText.Text = "Aktualisiere … TerminalMgr startet gleich neu.";
            Close();
        }
        catch (Exception ex)
        {
            _updating = false;
            ShowError("Der Updater konnte nicht gestartet werden. Die installierte Version bleibt unverändert.", ex);
        }
    }
}
