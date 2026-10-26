using System.IO;
using System.Windows;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Remembers where the window was, how big, maximized or not, and which side panels were open (see <see cref="WindowLayout"/>).</summary>
public partial class MainWindow
{
    private bool _layoutReady;

    /// <summary>Before the window is shown: put it where it was, if that place still exists on the desktop.</summary>
    private void RestoreWindowPlacement()
    {
        try
        {
            if (_store.Load().Window is not { } layout) return;
            var primary = SystemParameters.WorkArea;
            var screens = new[]
            {
                new DesktopArea(primary.Left, primary.Top, primary.Width, primary.Height),
                new DesktopArea(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            };
            if (layout.Fit(screens) is not { } area) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = area.X; Top = area.Y; Width = area.Width; Height = area.Height;
            if (layout.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { /* The settings error is reported when the window loads; here the default placement is fine. */ }
    }

    /// <summary>The current placement and panel state. A maximized or minimized window is saved with its normal bounds.</summary>
    private WindowLayout CaptureLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        // In focus mode the panels are hidden on purpose; what they were before is what comes back.
        return new WindowLayout
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            ServerOpen = _focusMode ? _focusRestoreServer : ServerPanelOpen,
            ServerWidth = ServerPanelOpen ? ServerColumn.ActualWidth : _serverWidth,
            ClipboardOpen = _focusMode ? _focusRestoreClipboard : ClipboardOpen,
            ClipboardWidth = ClipboardOpen ? ClipboardColumn.ActualWidth : _clipWidth
        };
    }

    /// <summary>After the settings are loaded: widths and open state of the panels; without a saved state the first-start rule decides.</summary>
    private void RestorePanels(bool serverOpenByDefault)
    {
        var layout = _settings.Window;
        _serverWidth = WindowLayout.PanelWidth(layout?.ServerWidth ?? 0, ServerPanelWidth, 180);
        _clipWidth = WindowLayout.PanelWidth(layout?.ClipboardWidth ?? 0, ClipboardPanelWidth, 220);
        SetServerPanel(layout?.ServerOpen ?? serverOpenByDefault);
        if (layout?.ClipboardOpen is { } clipboard) SetClipboard(clipboard);
        _layoutReady = true;
    }
}
