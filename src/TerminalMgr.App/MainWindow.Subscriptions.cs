using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

public partial class MainWindow
{
    private readonly IAiAccountSource _aiSource;
    private CancellationTokenSource? _aiRead;
    private DispatcherTimer? _aiTimer;
    private bool _aiLocal, _aiCollapsed, _aiBusy;
    private int _aiGeneration;
    private double _aiHeight = 220;
    private string? _aiKey;
    private IReadOnlyList<AiAccountSnapshot> _aiAccounts = [];
    private readonly Dictionary<string, IReadOnlyList<AiAccountSnapshot>> _aiCache = [];
    private readonly Dictionary<string, (int Failures, DateTimeOffset Next)> _aiRetry = [];

    private void InitializeSubscriptions()
    {
        AiAccounts.DetailsRequested += account => new AiAccountDetailsWindow(account, AiSourceLabel.Text) { Owner = this }.Show();
        ServerPanel.IsVisibleChanged += (_, _) => { if (ServerPanel.IsVisible) ChangeAiSource(); else CancelAiRead(); };
        _aiTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, async (_, _) => {
            if (ServerPanel.IsVisible && !_aiCollapsed && !_aiBusy) await RefreshAiAsync();
        }, Dispatcher);
        _aiTimer.Start();
    }

    private void CancelAiRead() { _aiGeneration++; _aiRead?.Cancel(); _aiRead = null; _aiBusy = false; }
    private void ChangeAiSource()
    {
        CancelAiRead();
        Profile? profile = _aiLocal ? null : Selected?.Profile;
        _aiKey = _aiLocal ? "local" : profile?.Id;
        AiSourceLabel.Text = _aiLocal ? "Dieser PC · lokale Konten" : profile?.Name + " · kontoweite Nutzung";
        if (_aiKey is null) { AiSourceLabel.Text = "Server auswählen oder Dieser PC über ⋯"; _aiAccounts = []; }
        else _aiAccounts = _aiCache.GetValueOrDefault(_aiKey) ?? [];
        RenderAi();
        if (_layoutReady && ServerPanel.IsVisible && !_aiCollapsed && _aiKey is not null) _ = RefreshAiAsync();
    }

    private async Task RefreshAiAsync(bool force = false)
    {
        if (_closing || _aiKey is null || _aiBusy) return;
        if (!force && _aiRetry.TryGetValue(_aiKey, out var retry) && retry.Next > DateTimeOffset.UtcNow) return;
        CancelAiRead();
        int generation = _aiGeneration;
        string key = _aiKey;
        Profile? profile = _aiLocal ? null : Selected?.Profile;
        var cancel = _aiRead = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _aiBusy = true;
        AiCount.Text = "…";
        try
        {
            var result = (await _aiSource.ReadAsync(profile, cancel.Token)).ToList();
            if (profile is null)
            {
                string? secret = AiCredentialStore.ReadOpenRouter();
                if (secret is not null)
                {
                    try { result.Add(await AiAccountSource.ReadOpenRouterAsync(secret, cancel.Token)); }
                    catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or OperationCanceledException)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        result.Add(AiAccountSnapshot.Unavailable("OpenRouter", "Abfrage fehlgeschlagen") with { Error = "OpenRouter nicht erreichbar · später erneut versuchen" });
                    }
                }
            }
            if (generation != _aiGeneration || _closing) return;
            // Preserve old values only within this source, visibly marked as failed/stale.
            _aiAccounts = result.Select(a => a.Error is not null && _aiAccounts.FirstOrDefault(old => old.Provider == a.Provider && old.ObservedAt is not null) is { } previous
                ? previous with { Error = a.Error } : a).ToArray();
            _aiCache[key] = _aiAccounts;
            int failures = result.Any(a => a.Error is not null) ? _aiRetry.GetValueOrDefault(key).Failures + 1 : 0;
            _aiRetry[key] = (failures, DateTimeOffset.UtcNow.AddSeconds(failures == 0 ? 60 : Math.Min(600, 60 * Math.Pow(2, Math.Min(failures, 4)))));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.IO.IOException)
        { if (generation == _aiGeneration) AiSourceLabel.Text = "Kontodaten nicht erreichbar · Setup über ⋯"; }
        finally
        {
            cancel.Dispose();
            if (generation == _aiGeneration) { _aiRead = null; _aiBusy = false; RenderAi(); }
        }
    }

    private void RenderAi()
    {
        AiAccounts.Render(_aiAccounts, DateTimeOffset.UtcNow);
        AiCount.Text = _aiAccounts.Count == 0 ? "" : _aiAccounts.Count + " Anbieter";
    }
    private void ToggleAiPanel(object sender, RoutedEventArgs e)
    {
        _aiCollapsed = !_aiCollapsed;
        ApplyAiHeight(); SaveSettings();
        if (!_aiCollapsed) ChangeAiSource(); else CancelAiRead();
    }
    private void AiAreaSizeChanged(object sender, SizeChangedEventArgs e) => ApplyAiHeight();
    private void ApplyAiHeight()
    {
        if (AiPanelRow is null || ServerContent is null || AiPanelBody is null) return;
        double height = Math.Min(_aiHeight, Math.Max(36, ServerContent.ActualHeight - 69));
        AiPanelRow.Height = new GridLength(_aiCollapsed ? 36 : height);
        AiPanelBody.Visibility = _aiCollapsed ? Visibility.Collapsed : Visibility.Visible;
        AiCollapseButton.Content = (_aiCollapsed ? "›" : "⌄") + "  KI-ABOS";
        AiAccountsScroll.MaxHeight = Math.Max(0, height - 60);
    }
    private void AiSplitChanged(object sender, DragCompletedEventArgs e)
    {
        if (!_aiCollapsed) _aiHeight = Math.Clamp(AiPanelRow.ActualHeight, 80, 600);
        ApplyAiHeight(); SaveSettings();
    }
    private void AiMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "Aktualisieren", IsEnabled = !_aiBusy && _aiKey is not null };
        refresh.Click += async (_, _) => await RefreshAiAsync(force: true); menu.Items.Add(refresh);
        foreach (bool local in new[] { false, true })
        {
            var source = new MenuItem { Header = local ? "Dieser PC" : "Ausgewählter SSH-Server", IsCheckable = true, IsChecked = _aiLocal == local };
            source.Click += (_, _) => { _aiLocal = local; ChangeAiSource(); }; menu.Items.Add(source);
        }
        menu.Items.Add(new Separator());
        var setup = new MenuItem { Header = "KI-Anbieter einrichten …" };
        setup.Click += (_, _) => OpenAiSetup(this); menu.Items.Add(setup);
        menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }
    private void OpenAiSetup(Window owner)
    {
        var dialog = new AiProvidersDialog(_profiles.Select(p => p.Profile).ToArray(), _aiLocal ? null : Selected?.Profile) { Owner = owner };
        dialog.ShowDialog();
        _aiRetry.Clear();
        ChangeAiSource();
    }
}
