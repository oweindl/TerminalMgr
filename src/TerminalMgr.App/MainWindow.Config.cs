using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Export and import of the configuration (.tmconfig). The logic is in Core; this part does the dialogs and the files.</summary>
public partial class MainWindow
{
    private const string ConfigFilter = "TerminalMgr-Konfiguration (*.tmconfig)|*.tmconfig|Alle Dateien (*.*)|*.*";

    private async Task ExportConfigAsync()
    {
        try
        {
            SaveSettings();
            var hosts = await Task.Run(() => SshConfigBlocks.ForProfiles(_settings.Profiles));
            var dialog = new ExportDialog(_settings, hosts) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Selection is not { } selection) return;
            var save = new SaveFileDialog { Filter = ConfigFilter, DefaultExt = ".tmconfig", FileName = $"TerminalMgr-{DateTime.Now:yyyyMMdd}.tmconfig", Title = "Konfiguration exportieren" };
            if (save.ShowDialog(this) != true) return;
            var content = ConfigFile.Build(_settings, selection, hosts);
            byte[] file = ConfigFile.Serialize(content, AppInfo.Version, DateTimeOffset.UtcNow, dialog.Password);
            await File.WriteAllBytesAsync(save.FileName, file);
            StatusText.Text = $"Konfiguration exportiert: {save.FileName}" + (dialog.Password is null ? " (ohne Passwort)" : " (verschlüsselt)");
        }
        catch (Exception ex) { ShowError("Die Konfiguration konnte nicht exportiert werden.", ex); }
    }

    private async Task ImportConfigAsync()
    {
        try
        {
            var open = new OpenFileDialog { Filter = ConfigFilter, Title = "Konfiguration importieren" };
            if (open.ShowDialog(this) != true) return;
            byte[] file = await File.ReadAllBytesAsync(open.FileName);
            var info = ConfigFile.Inspect(file);
            string? password = null;
            string? error = null;
            ConfigContent content;
            while (true)
            {
                if (info.Encrypted)
                {
                    var ask = new PasswordDialog("Die Datei ist passwortgeschützt. Passwort eingeben:", error) { Owner = this };
                    if (ask.ShowDialog() != true) return;
                    password = ask.Password;
                }
                try { content = ConfigFile.Parse(file, password).Content; break; }
                catch (ConfigPasswordException ex) when (info.Encrypted) { error = ex.Message; }
            }

            string sshConfig = SshConfigBlocks.DefaultConfigPath();
            var existingHosts = await Task.Run(() => SshConfigBlocks.ExistingAliases(sshConfig));
            var dialog = new ImportDialog(_settings, content, info, existingHosts) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var result = ConfigImporter.Apply(_settings, content, dialog.Plan, dialog.Choices, dialog.TakeProfiles, dialog.TakeSettings, dialog.TakePinned, dialog.TakeHosts);
            var now = DateTimeOffset.Now;
            string? settingsBackup = ConfigBackup.BackupSettings(_dataDirectory, now);
            string? sshBackup = null;
            if (result.HostBlocksToAdd.Count > 0)
            {
                sshBackup = ConfigBackup.BackupSshConfig(sshConfig, now);
                Directory.CreateDirectory(Path.GetDirectoryName(sshConfig)!);
                string existing = File.Exists(sshConfig) ? await File.ReadAllTextAsync(sshConfig) : "";
                await File.WriteAllTextAsync(sshConfig, SshConfigBlocks.AppendBlocks(existing, result.HostBlocksToAdd, now), new UTF8Encoding(false));
            }

            ApplyImported(result.Settings);
            var problems = await CheckImportedAsync(result.Added.Concat(result.Replaced).Concat(result.Copied).ToList());
            var summary = new StringBuilder();
            summary.AppendLine($"Profile: {result.Added.Count} neu, {result.Replaced.Count} ersetzt, {result.Copied.Count} als Kopie, {result.Skipped.Count} übersprungen.");
            if (result.HostBlocksToAdd.Count > 0) summary.AppendLine($"SSH-Host-Blöcke angehängt: {result.HostBlocksToAdd.Count}.");
            if (settingsBackup is not null) summary.AppendLine("Sicherung der Einstellungen: " + settingsBackup);
            if (sshBackup is not null) summary.AppendLine("Sicherung der SSH-Config: " + sshBackup);
            if (problems.Count > 0)
            {
                summary.AppendLine().AppendLine("Das blockiert die Anmeldung noch:");
                foreach (var p in problems) summary.AppendLine($"• {p.ProfileName}: {p.Message}");
            }
            StatusText.Text = "Konfiguration importiert.";
            MessageBox.Show(this, summary.ToString().TrimEnd(), "Import abgeschlossen", MessageBoxButton.OK, problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ConfigFormatException or ConfigPasswordException)
        { MessageBox.Show(this, ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { ShowError("Die Konfiguration konnte nicht importiert werden.", ex); }
    }

    /// <summary>The imported state replaces the lists in memory; open sessions keep running with the profile they were started from.</summary>
    private void ApplyImported(AppSettings settings)
    {
        _settings = settings;
        string? selected = (ProfilesList.SelectedItem as ProfileItem)?.Profile.Id;
        _profiles.Clear();
        foreach (var profile in settings.Profiles) _profiles.Add(new(profile));
        if (_profiles.FirstOrDefault(p => p.Profile.Id == selected) is { } same) ProfilesList.SelectedItem = same;
        else if (_profiles.Count > 0) ProfilesList.SelectedIndex = 0;
        _clipList.MaxUnpinned = Math.Clamp(settings.ClipHistoryLimit, 1, 1000);
        _clipList.LoadPinned(settings.PinnedClips);
        PostShortcuts();
        PostTerminalSettings();
        ApplySettingsToUi();
        RenderClips();
        SaveSettings();
    }

    /// <summary>ssh -G for each imported profile: missing key files and undefined hosts are reported right away.</summary>
    private async Task<List<ProfileProblem>> CheckImportedAsync(IReadOnlyList<Profile> profiles)
    {
        var problems = new List<ProfileProblem>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var profile in profiles)
        {
            try
            {
                if (await _tmuxQuery.EffectiveConfigAsync(profile) is { } output)
                    problems.AddRange(ConfigCheck.Evaluate(profile, output, File.Exists, home));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { problems.Add(new(profile.Name, ex.Message)); }
        }
        return problems;
    }
}
