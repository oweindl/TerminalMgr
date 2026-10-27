namespace TerminalMgr.Core;

/// <summary>A rectangle on the desktop in device-independent pixels (a screen's work area, or the window).</summary>
public readonly record struct DesktopArea(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public DesktopArea? Intersect(DesktopArea other)
    {
        double left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        double right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new DesktopArea(left, top, right - left, bottom - top) : null;
    }
}

/// <summary>
/// Where the window was and what was open when TerminalMgr closed: position, size, maximized, the two side panels with their widths.
/// A null open flag means "never saved", so the first start keeps its own rule.
/// </summary>
public sealed record WindowLayout
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Maximized { get; init; }
    public bool? ServerOpen { get; init; }
    public double ServerWidth { get; init; }
    public bool? ClipboardOpen { get; init; }
    public double ClipboardWidth { get; init; }
    /// <summary>The strip of tmux windows below the title bar; null (never saved) means on.</summary>
    public bool? StripOpen { get; init; }
    public double AiPanelHeight { get; init; } = 220;
    public bool AiPanelCollapsed { get; init; }

    public const double MinWidth = 880, MinHeight = 540;
    private const double VisibleWidth = 160, VisibleHeight = 60;

    /// <summary>
    /// The saved rectangle made safe for the screens that exist now (a monitor may be gone, the resolution may have changed):
    /// at least the minimum size, no bigger than the first screen, and with a usable part of the title bar on a screen. If it is
    /// not on any screen it is centered on the first one. Null when nothing usable was saved.
    /// </summary>
    public DesktopArea? Fit(IReadOnlyList<DesktopArea> screens)
    {
        if (screens.Count == 0 || !double.IsFinite(Left + Top + Width + Height) || Width < 100 || Height < 100) return null;
        var main = screens[0];
        double width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, main.Width)), height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, main.Height));
        var rect = new DesktopArea(Left, Top, width, height);
        // The strip at the top is what the user grabs to move the window.
        var grip = new DesktopArea(rect.X, rect.Y, rect.Width, Math.Min(VisibleHeight, rect.Height));
        foreach (var screen in screens)
            if (grip.Intersect(screen) is { } shared && shared.Width >= Math.Min(VisibleWidth, rect.Width) && shared.Height >= grip.Height - 1)
                return rect;
        width = Math.Min(width, main.Width); height = Math.Min(height, main.Height);
        return new DesktopArea(main.X + (main.Width - width) / 2, main.Y + (main.Height - height) / 2, width, height);
    }

    /// <summary>A panel width that is sane: a saved 0 or garbage falls back to the default.</summary>
    public static double PanelWidth(double saved, double fallback, double min) =>
        double.IsFinite(saved) && saved >= min ? Math.Min(saved, 1200) : fallback;
}
