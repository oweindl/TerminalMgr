using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

public enum UpdateChoice { Later, Skip, Installed }

/// <summary>"A new version is available": release notes, install now / later / skip this version, and the download progress.</summary>
public sealed class UpdateDialog : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
    private readonly StackPanel _progress = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 12, Minimum = 0, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0xD4, 0xB7)), Background = new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x32)), BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _cancel;
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;
    /// <summary>The prepared, verified new version (a folder with TerminalMgr.App.exe) after "Jetzt aktualisieren".</summary>
    public string? StagedApp { get; private set; }

    /// <param name="stage">Downloads and verifies the release; reports progress; returns the prepared folder.</param>
    public UpdateDialog(UpdateRelease release, Version current, bool canInstall, string installDirectory,
        Func<IProgress<(long Done, long Total)>, CancellationToken, Task<string>> stage)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Update verfügbar";
        Width = 560; SizeToContent = SizeToContent.Height; MaxHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = $"TerminalMgr {release.Version} ist verfügbar", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Installiert ist Version {current}.", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 4, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Neu in dieser Version:" });
        panel.Children.Add(new TextBox { Text = string.IsNullOrWhiteSpace(release.Notes) ? "(keine Release-Notes)" : release.Notes.Replace("\r\n", "\n").Trim(),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 220, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
        if (!canInstall)
            panel.Children.Add(new TextBlock { Foreground = Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Text = $"Diese App läuft nicht aus {installDirectory} (Entwicklungs-Build oder Kopie). Es wird nichts ausgetauscht; installieren Sie die neue Version mit scripts\\Install-Local.ps1 oder aus dem Release." });
        panel.Children.Add(_buttons);
        _progress.Children.Add(_status);
        _progress.Children.Add(_bar);
        panel.Children.Add(_progress);

        var install = new Button { Content = "Jetzt aktualisieren", IsDefault = true, IsEnabled = canInstall, ToolTip = "Lädt die neue Version, prüft die Prüfsumme und startet TerminalMgr neu. Offene SSH-Verbindungen werden getrennt." };
        install.Click += async (_, _) =>
        {
            _buttons.Visibility = Visibility.Collapsed;
            _progress.Visibility = Visibility.Visible;
            _bar.Visibility = Visibility.Visible;
            _status.Text = "Lade die neue Version …";
            _cancel = new CancellationTokenSource();
            var cancelButton = new Button { Content = "Abbrechen", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            cancelButton.Click += (_, _) => _cancel.Cancel();
            _progress.Children.Add(cancelButton);
            try
            {
                var reporter = new Progress<(long Done, long Total)>(p => {
                    _bar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                    _status.Text = $"Lade die neue Version … {p.Done / 1_000_000} von {p.Total / 1_000_000} MB";
                });
                StagedApp = await stage(reporter, _cancel.Token);
                Choice = UpdateChoice.Installed;
                DialogResult = true;
            }
            catch (OperationCanceledException) { _progress.Children.Remove(cancelButton); Reset("Abgebrochen. Die installierte Version bleibt unverändert."); }
            catch (Exception ex) { _progress.Children.Remove(cancelButton); Reset("Das Update ist fehlgeschlagen: " + ex.Message + "\nDie installierte Version bleibt unverändert."); }
        };
        var later = new Button { Content = "Später", IsCancel = true };
        later.Click += (_, _) => { Choice = UpdateChoice.Later; DialogResult = false; };
        var skip = new Button { Content = "Diese Version überspringen" };
        skip.Click += (_, _) => { Choice = UpdateChoice.Skip; DialogResult = false; };
        _buttons.Children.Add(install);
        _buttons.Children.Add(later);
        _buttons.Children.Add(skip);
    }

    private void Reset(string message)
    {
        _bar.Value = 0;
        _status.Text = message;
        _buttons.Visibility = Visibility.Visible;
        // Keep the message visible; only the progress bar hides.
        _bar.Visibility = Visibility.Collapsed;
    }
}
