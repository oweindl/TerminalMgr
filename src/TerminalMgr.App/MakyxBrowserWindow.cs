using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// The Makyx interface inside TerminalMgr (note 15, stage 1): one window, reused for every link. It shows only the Makyx pages it was asked
/// to open (same origin); other addresses open in the default browser. Cookies live in an own WebView2 folder, so the Makyx login stays.
/// </summary>
public sealed class MakyxBrowserWindow : Window
{
    private static MakyxBrowserWindow? _current;
    private readonly WebView2 _view = new();
    private readonly TextBlock _address = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0), FontSize = 12 };
    private readonly Button _back = new() { Content = "←", ToolTip = "Zurück", Width = 32, IsEnabled = false };
    private readonly HashSet<string> _origins = new(StringComparer.Ordinal);
    private readonly string _dataDirectory;
    private readonly Func<string?> _fingerprint;
    private Task? _ready;
    private string? _pending;

    /// <summary>Opens the link in the Makyx window (created on first use, then reused and brought to the front).</summary>
    public static void Open(Window owner, string dataDirectory, Func<string?> caFingerprint, string link)
    {
        if (MakyxBrowserRules.Origin(link) is null) return;
        if (_current is null)
        {
            var window = new MakyxBrowserWindow(dataDirectory, caFingerprint) { Left = owner.Left + 60, Top = owner.Top + 40 };
            _current = window;
            window.Closed += (_, _) => _current = null;
            // Hidden on close (keeps the Makyx login of this run); closed for good together with the main window, so the app can end.
            owner.Closed += (_, _) => window.CloseForGood();
        }
        if (!_current.IsVisible) _current.Show();
        if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
        _current.Activate();
        _ = _current.NavigateAsync(link);
    }

    private MakyxBrowserWindow(string dataDirectory, Func<string?> caFingerprint)
    {
        _dataDirectory = dataDirectory; _fingerprint = caFingerprint;
        Title = "Makyx"; Width = 1280; Height = 860; MinWidth = 640; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/TerminalMgr.ico")); } catch (Exception ex) when (ex is IOException or NotSupportedException) { }
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var reload = new Button { Content = "⟳", ToolTip = "Neu laden", Width = 32, Margin = new Thickness(4, 0, 0, 0) };
        var external = new Button { Content = "Im Browser öffnen", Padding = new Thickness(10, 0, 10, 0) };
        foreach (var button in new[] { _back, reload, external }) { button.SetResourceReference(StyleProperty, "FlatButton"); button.Height = 28; }
        _back.Click += (_, _) => { if (_view.CoreWebView2?.CanGoBack == true) _view.CoreWebView2.GoBack(); };
        reload.Click += (_, _) => _view.CoreWebView2?.Reload();
        external.Click += (_, _) => OpenExternal(_view.CoreWebView2?.Source ?? _pending);
        _address.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        Label(_back, "Zurück"); Label(reload, "Neu laden"); Label(external, "Im Browser öffnen");

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = true };
        DockPanel.SetDock(_back, Dock.Left); DockPanel.SetDock(reload, Dock.Left); DockPanel.SetDock(external, Dock.Right);
        bar.Children.Add(_back); bar.Children.Add(reload); bar.Children.Add(external); bar.Children.Add(_address);
        var top = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        top.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top); root.Children.Add(_view);
        Content = root;
    }

    private bool _closeForGood;

    /// <summary>Closing only hides the window: the WebView2 process and with it the Makyx session cookie stay for this app run.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeForGood) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }

    private void CloseForGood()
    {
        _closeForGood = true;
        Close();
    }

    private static void Label(UIElement element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private Task EnsureReadyAsync() => _ready ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDirectory, "WebView2-Makyx"));
        await _view.EnsureCoreWebView2Async(environment);
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, args) => {
            if (MakyxBrowserRules.IsAllowed(args.Uri, _origins)) return;
            args.Cancel = true;
            OpenExternal(args.Uri);
        };
        core.NewWindowRequested += (_, args) => {
            args.Handled = true;
            if (MakyxBrowserRules.IsAllowed(args.Uri, _origins)) core.Navigate(args.Uri); else OpenExternal(args.Uri);
        };
        core.SourceChanged += (_, _) => { _address.Text = core.Source; _back.IsEnabled = core.CanGoBack; };
        core.HistoryChanged += (_, _) => _back.IsEnabled = core.CanGoBack;
        core.DocumentTitleChanged += (_, _) => Title = core.DocumentTitle is { Length: > 0 } t ? "Makyx · " + t : "Makyx";
        // Own Makyx CA: allowed only for the Makyx pages and only when the CA matches the fingerprint of the settings.
        core.ServerCertificateErrorDetected += (_, args) => {
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            if (!MakyxBrowserRules.IsAllowed(args.RequestUri, _origins)) return;
            try
            {
                var chain = new List<byte[]> { args.ServerCertificate.ToX509Certificate2().RawData };
                foreach (string pem in args.ServerCertificate.PemEncodedIssuerCertificateChain)
                    using (var issuer = X509Certificate2.CreateFromPem(pem)) chain.Add(issuer.RawData);
                if (MakyxBrowserRules.ChainMatches(chain, _fingerprint())) args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException) { }
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
    }

    private async Task NavigateAsync(string link)
    {
        if (MakyxBrowserRules.Origin(link) is not { } origin) return;
        _origins.Add(origin);
        _pending = link;
        _address.Text = link;
        try
        {
            await EnsureReadyAsync();
            _view.CoreWebView2.Navigate(link);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            OpenExternal(link);
            CloseForGood();
        }
    }

    /// <summary>Only http and https, in the default browser.</summary>
    private static void OpenExternal(string? link)
    {
        string safe = MakyxJson.SafeLink(link);
        if (safe.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
