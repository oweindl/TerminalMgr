using System.Windows;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Setting up the Makyx dashboard from a pasted configuration text (note 14): read, preview, apply.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>"Einlesen": the pasted text is checked and shown as a preview; nothing is stored yet.</summary>
    private void ReadMakyxConfig()
    {
        _pendingConfig = null;
        _makyxPreviewBox.Visibility = Visibility.Collapsed;
        _makyxClearClipboard.Visibility = Visibility.Collapsed;
        var result = MakyxConfigText.Parse(_makyxConfig.Text, DateTimeOffset.UtcNow);
        if (result.Config is not { } config) { _makyxResult.Text = result.Error ?? "Der Text ließ sich nicht lesen."; return; }
        _pendingConfig = config;
        var lines = MakyxConfigText.Preview(config).ToList();
        if (!config.ApiAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) lines.Add("Achtung: Die Verbindung ist nicht verschlüsselt (http). Nur im eigenen Netz verwenden.");
        _makyxPreview.Text = string.Join(Environment.NewLine, lines);
        _makyxPreviewBox.Visibility = Visibility.Visible;
        _makyxResult.Text = "Bitte die Angaben prüfen, vor allem den CA-Fingerprint gegen Makyx, dann „Übernehmen“.";
    }

    private void ReadMakyxConfigFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Makyx-Konfiguration öffnen", Filter = "Makyx-Konfiguration (*.makyxleitstand;*.json;*.txt)|*.makyxleitstand;*.json;*.txt|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(dialog.FileName).Length > MakyxConfigText.MaxLength) { _makyxResult.Text = "Die Datei ist zu groß für eine Makyx-Konfiguration."; return; }
            _makyxConfig.Text = System.IO.File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _makyxResult.Text = "Die Datei ließ sich nicht lesen."; return; }
        ReadMakyxConfig();
    }

    private bool HasMakyxConnection()
    {
        if (_makyxEnabled.IsChecked == true && _makyxAddress.Text.Trim().Length > 0) return true;
        try { return MakyxCredentialStore.Hint() is not null; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// "Übernehmen": the one-time code is exchanged for the token, the token goes only into the Windows credential store, address and CA into the
    /// settings. Then the connection is checked; with a code that worked, the settings are saved so the strip appears.
    /// </summary>
    private async Task ApplyMakyxConfigAsync()
    {
        if (_pendingConfig is not { } config) return;
        if (HasMakyxConnection())
        {
            bool replace = ConfirmReplace?.Invoke("Bestehenden Leitstand ersetzen?")
                ?? MessageBox.Show(this, "Bestehenden Leitstand ersetzen?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!replace) { _makyxResult.Text = "Nicht ersetzt."; return; }
        }
        _makyxApply.IsEnabled = false;
        string? token = config.Token;
        try
        {
            var options = (MakyxFromFields() with { Address = config.Address, CaFingerprint = config.CaFingerprint, Enabled = true }).Normalized();
            if (options.Problem() is { } problem) { _makyxResult.Text = problem; return; }
            if (token is null)
            {
                _makyxResult.Text = "Löse den Einmal-Code ein …";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                token = await MakyxClient.RedeemAsync(options, config.Code!, timeout.Token);
            }
            try { MakyxCredentialStore.Save(token); }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
            { _makyxResult.Text = "Das Token ließ sich nicht im Windows-Anmeldeinformationsspeicher ablegen. Der Code ist dabei verbraucht; bitte in Makyx die Konfiguration neu erzeugen."; return; }
            _makyxAddress.Text = options.Address; _makyxFingerprint.Text = options.CaFingerprint; _makyxEnabled.IsChecked = true; _makyxToken.Clear();
            _makyxConfig.Clear(); _pendingConfig = null; _makyxPreviewBox.Visibility = Visibility.Collapsed;
            if (!await CheckMakyxAsync(null)) return;
            if (config.HasToken)
            {
                _makyxClearClipboard.Visibility = Visibility.Visible;
                _makyxResult.Text += " Das Token stand im Text: bitte die Zwischenablage leeren und danach „Speichern“.";
            }
            else Save();
        }
        catch (MakyxClient.MakyxException ex) { _makyxResult.Text = ex.Message; }
        catch (Exception ex) when (ex is ArgumentException or OperationCanceledException) { _makyxResult.Text = "Makyx antwortet nicht (Zeitlimit) oder die Adresse ist ungültig."; }
        finally { _makyxApply.IsEnabled = true; }
    }
}
