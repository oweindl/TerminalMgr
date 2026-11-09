using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalMgr.App;

/// <summary>A small notification window at the top right of the work area: it never takes the focus, closes after 12 seconds or with the cross, and a click runs the action.
/// At most three are shown at once; a fourth replaces the oldest.</summary>
public sealed class ToastWindow : Window
{
    private static readonly List<ToastWindow> Open = [];
    private readonly DispatcherTimer _timer;
    public const int MaxOpen = 3;

    public static void Show(string title, string text, Action? onClick)
    {
        while (Open.Count >= MaxOpen) Open[0].Close();
        var toast = new ToastWindow(title, text, onClick);
        Open.Add(toast);
        toast.Place();
        toast.Show();
    }

    private ToastWindow(string title, string text, Action? onClick)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true; AllowsTransparency = false;
        Width = 340; SizeToContent = SizeToContent.Height; Focusable = false;
        System.Windows.Automation.AutomationProperties.SetName(this, title + ": " + text);
        SetResourceReference(BackgroundProperty, "BarBrush");
        var head = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA1, 0x1B)) };
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 60 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var close = new Button { Content = "✕", Width = 24, Height = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Schließen" };
        close.SetResourceReference(StyleProperty, "FlatButton");
        close.Click += (_, e) => { e.Handled = true; Close(); };
        System.Windows.Automation.AutomationProperties.SetName(close, "Meldung schließen");
        var content = new StackPanel(); content.Children.Add(head); content.Children.Add(body);
        var dock = new DockPanel { Margin = new Thickness(12, 8, 8, 10) };
        DockPanel.SetDock(close, Dock.Right); dock.Children.Add(close); dock.Children.Add(content);
        var border = new Border { BorderThickness = new Thickness(1), Child = dock, Cursor = System.Windows.Input.Cursors.Hand };
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrongBrush");
        Content = border;
        MouseLeftButtonUp += (_, _) => { onClick?.Invoke(); Close(); };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(12), DispatcherPriority.Background, (_, _) => Close(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Open.Remove(this); Arrange(); };
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16 + Open.IndexOf(this) * 86;
    }

    private static void Arrange() { foreach (var t in Open) t.Place(); }
}
