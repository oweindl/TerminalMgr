using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Compact list of Makyx runs under the server list: status dot, number and title, below project, machine, duration and cost. Click opens the Makyx page, right click offers the terminal.</summary>
public sealed class MakyxRunsPanel : StackPanel
{
    public event Action<MakyxRun>? OpenRequested;
    public event Action<MakyxRun>? TerminalRequested;
    public event Action<MakyxRun>? CopyRequested;
    public event Action<MakyxRun>? BrowserRequested;
    /// <summary>The terminal entry is enabled when this returns null (a reason otherwise).</summary>
    public Func<MakyxRun, string?>? TerminalProblem { get; set; }

    /// <summary>Dot colors as in Makyx: working cyan/blue, waiting amber, done green, failed red, blocked grey.</summary>
    public static Brush DotBrush(MakyxStatus status) => status switch
    {
        MakyxStatus.Working => new SolidColorBrush(Color.FromRgb(0x2B, 0xA4, 0xC8)),
        MakyxStatus.Waiting => new SolidColorBrush(Color.FromRgb(0xE0, 0xA1, 0x1B)),
        MakyxStatus.Done => new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0x6B)),
        MakyxStatus.Failed => new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x4F)),
        _ => new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0x9B))
    };

    private static TextBlock Text(string text, bool muted, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Text2Brush" : "TextBrush");
        return block;
    }

    public static string Duration(int? seconds) => seconds switch
    {
        null => "", < 60 => $"{seconds} s", < 3600 => $"{seconds / 60}:{seconds % 60:00} min", _ => $"{seconds / 3600} h {(seconds % 3600) / 60} min"
    };

    public static string Detail(MakyxRun run)
    {
        var parts = new List<string> { run.Project };
        if (run.Machine is { Length: > 0 }) parts.Add(run.Machine);
        if (Duration(run.DurationSeconds) is { Length: > 0 } d) parts.Add(d);
        if (run.CostUsd is { } c) parts.Add(c.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " USD");
        return string.Join(" · ", parts);
    }

    public void Render(IReadOnlyList<MakyxRun> runs, string emptyText)
    {
        Children.Clear();
        if (runs.Count == 0)
        {
            var empty = Text(emptyText, true, 11); empty.Margin = new Thickness(12, 4, 12, 8); empty.TextWrapping = TextWrapping.Wrap;
            Children.Add(empty);
            return;
        }
        foreach (var run in runs)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = DotBrush(run.Status), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0) };
            var title = Text($"{run.Id}  {run.Title}", false, 12); title.FontWeight = FontWeights.SemiBold;
            var status = Text(MakyxJson.StatusText(run.Status), run.Status != MakyxStatus.Waiting, 11);
            if (run.Status == MakyxStatus.Waiting) status.Foreground = DotBrush(MakyxStatus.Waiting);
            var detail = Text(Detail(run), true, 11);
            var head = new DockPanel(); DockPanel.SetDock(status, Dock.Right); status.Margin = new Thickness(6, 0, 0, 0); head.Children.Add(status); head.Children.Add(title);
            var content = new StackPanel(); content.Children.Add(head); content.Children.Add(detail);
            var grid = new DockPanel(); DockPanel.SetDock(dot, Dock.Left); grid.Children.Add(dot); grid.Children.Add(content);
            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5, 12, 6), Child = grid, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            string tooltip = $"{run.Id} · {run.Title} · {MakyxJson.StatusText(run.Status)}\n{Detail(run)}" + (run.Terminal.Available ? "\nLäuft in einem tmux-Fenster" : run.Terminal.Reason == "strukturiert" ? "\nKein Terminal (strukturierte Session)" : "");
            border.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(border, $"{run.Id} {run.Title}, {MakyxJson.StatusText(run.Status)}, {Detail(run)}");
            border.MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke(run);
            border.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { OpenRequested?.Invoke(run); e.Handled = true; } };
            border.ContextMenu = BuildMenu(run);
            Children.Add(border);
        }
    }

    private ContextMenu BuildMenu(MakyxRun run)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "In Makyx öffnen", IsEnabled = run.Link.Length > 0 };
        open.Click += (_, _) => OpenRequested?.Invoke(run);
        string? problem = run.Terminal.Available ? TerminalProblem?.Invoke(run) : "kein Terminal" + (run.Terminal.Reason == "strukturiert" ? " (strukturierte Session)" : run.Terminal.Reason == "kein_tmux" ? " (Rechner meldet kein tmux)" : "");
        var terminal = new MenuItem { Header = "Im Terminal öffnen" + (problem is null ? "" : " – " + problem), IsEnabled = problem is null };
        terminal.Click += (_, _) => TerminalRequested?.Invoke(run);
        var copy = new MenuItem { Header = "Link kopieren", IsEnabled = run.Link.Length > 0 };
        copy.Click += (_, _) => CopyRequested?.Invoke(run);
        var browser = new MenuItem { Header = "Im Browser öffnen", IsEnabled = run.Link.Length > 0 };
        browser.Click += (_, _) => BrowserRequested?.Invoke(run);
        menu.Items.Add(open); menu.Items.Add(terminal); menu.Items.Add(browser); menu.Items.Add(copy);
        return menu;
    }
}
