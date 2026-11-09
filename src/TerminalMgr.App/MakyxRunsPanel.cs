using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// Makyx runs under the server list, grouped by project (note 16, mockup A): per project a line with name, count and "+", below it its active
/// sessions and runs ("ingest-api · Worker", status, Alt number of an open tab), then the recently finished runs. Click on a session opens its
/// terminal, on anything else the Makyx page; right click offers the rest.
/// </summary>
public sealed class MakyxRunsPanel : StackPanel
{
    public event Action<MakyxRun>? OpenRequested;
    /// <summary>Click or Enter on a run: the window opens the terminal when there is one, else the page.</summary>
    public event Action<MakyxRun>? ActivateRequested;
    /// <summary>"+" at a project or "Neue Session …" in its menu.</summary>
    public event Action<MakyxProject>? NewSessionRequested;
    public event Action<MakyxProject>? ProjectPageRequested;
    public event Action<MakyxProject>? StartRunRequested;
    /// <summary>Projects the person folded away (by key); projects without active runs start folded.</summary>
    private readonly HashSet<string> _folded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unfolded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MakyxRun> _runs = [];
    private IReadOnlyList<MakyxProject> _projects = [];
    private string _empty = "";
    private Func<MakyxRun, string?>? _altLabel;
    public const int RecentCount = 6;
    public event Action<MakyxRun>? TerminalRequested;
    public event Action<MakyxRun>? CopyRequested;
    public event Action<MakyxRun>? BrowserRequested;
    /// <summary>"Anhalten …" (stops the run; the window asks first).</summary>
    public event Action<MakyxRun>? StopRequested;
    /// <summary>"Nothalt (ganz Makyx) …" (stops all of Makyx; the window asks first).</summary>
    public event Action<MakyxRun>? EmergencyRequested;
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
        var parts = new List<string>();
        parts.Add(MakyxNames.ProjectLabel(run));
        if (run.Machine is { Length: > 0 }) parts.Add(run.Machine);
        if (Duration(run.DurationSeconds) is { Length: > 0 } d) parts.Add(d);
        if (run.CostUsd is { } c) parts.Add(c.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " USD");
        return string.Join(" · ", parts);
    }

    public void Render(IReadOnlyList<MakyxRun> runs, IReadOnlyList<MakyxProject> projects, string emptyText, Func<MakyxRun, string?>? altLabel = null)
    {
        _runs = runs; _projects = projects; _empty = emptyText; _altLabel = altLabel;
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (_runs.Count == 0 && _projects.Count == 0)
        {
            var empty = Text(_empty, true, 11); empty.Margin = new Thickness(12, 4, 12, 8); empty.TextWrapping = TextWrapping.Wrap;
            Children.Add(empty);
            return;
        }
        foreach (var group in MakyxGrouping.Groups(_runs, _projects))
        {
            bool open = group.Active.Count > 0 ? !_folded.Contains(group.Key) : _unfolded.Contains(group.Key);
            Children.Add(GroupHeader(group, open));
            if (open) foreach (var run in group.Active) Children.Add(SessionRow(run));
        }
        var recent = MakyxGrouping.Recent(_runs, RecentCount);
        if (recent.Count > 0)
        {
            var label = Text("ZULETZT", true, 11); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(12, 10, 12, 2);
            Children.Add(label);
            foreach (var run in recent) Children.Add(RunRow(run));
        }
    }

    private void Toggle(MakyxProjectGroup group, bool open)
    {
        if (group.Active.Count > 0) { if (open) _folded.Add(group.Key); else _folded.Remove(group.Key); }
        else { if (open) _unfolded.Remove(group.Key); else _unfolded.Add(group.Key); }
        Rebuild();
    }

    private FrameworkElement GroupHeader(MakyxProjectGroup group, bool open)
    {
        var chevron = Text(open ? "⌄" : "›", true, 12); chevron.Width = 14; chevron.Margin = new Thickness(0, open ? -4 : 0, 4, 0);
        var name = Text(group.Name, false, 12); name.FontWeight = FontWeights.SemiBold;
        var summary = Text(MakyxGrouping.Summary(group), group.Failed == 0 || group.Active.Count > 0, 11); summary.Margin = new Thickness(6, 0, 0, 0);
        if (group.Failed > 0 && group.Active.Count == 0) summary.Foreground = DotBrush(MakyxStatus.Failed);
        var row = new DockPanel { LastChildFill = true };
        if (group.Project is { } project)
        {
            var plus = new Button { Content = "+", Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Neue Session in " + group.Name, Focusable = true };
            plus.SetResourceReference(StyleProperty, "FlatButton");
            System.Windows.Automation.AutomationProperties.SetName(plus, "Neue Session in " + group.Name);
            plus.Click += (_, e) => { NewSessionRequested?.Invoke(project); e.Handled = true; };
            DockPanel.SetDock(plus, Dock.Right); row.Children.Add(plus);
        }
        DockPanel.SetDock(summary, Dock.Right); row.Children.Add(summary);
        DockPanel.SetDock(chevron, Dock.Left); row.Children.Add(chevron);
        row.Children.Add(name);
        var border = new Border { Padding = new Thickness(10, 5, 8, 5), Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        border.ToolTip = group.Name + " · " + MakyxGrouping.Summary(group) + (group.Waiting > 0 ? " · " + group.Waiting + " wartet" : "");
        System.Windows.Automation.AutomationProperties.SetName(border, "Projekt " + group.Name + ", " + MakyxGrouping.Summary(group) + (open ? ", aufgeklappt" : ", zugeklappt"));
        border.MouseLeftButtonUp += (_, _) => Toggle(group, open);
        border.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Toggle(group, open); e.Handled = true; } };
        if (group.Project is { } p)
        {
            var menu = new ContextMenu();
            var page = new MenuItem { Header = "Projektseite öffnen", IsEnabled = p.Link.Length > 0 };
            page.Click += (_, _) => ProjectPageRequested?.Invoke(p);
            var session = new MenuItem { Header = "Neue Session …" };
            session.Click += (_, _) => NewSessionRequested?.Invoke(p);
            var run = new MenuItem { Header = "Lauf starten …" };
            run.Click += (_, _) => StartRunRequested?.Invoke(p);
            menu.Items.Add(page); menu.Items.Add(session); menu.Items.Add(run);
            border.ContextMenu = menu;
        }
        return border;
    }

    /// <summary>An active session or run under its project: "ingest-api · Worker", status text, Alt number of its open tab.</summary>
    private FrameworkElement SessionRow(MakyxRun run)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = DotBrush(run.Status), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var shortName = Text(MakyxNames.ShortName(run), false, 12); shortName.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        var role = Text(MakyxNames.RoleText(run) is { } r ? " · " + r : "", true, 12);
        var names = new StackPanel { Orientation = Orientation.Horizontal }; names.Children.Add(shortName); names.Children.Add(role);
        var status = Text(MakyxJson.StatusText(run.Status), run.Status != MakyxStatus.Waiting, 11); status.Margin = new Thickness(6, 0, 0, 0);
        if (run.Status == MakyxStatus.Waiting) status.Foreground = DotBrush(MakyxStatus.Waiting);
        var row = new DockPanel();
        if (_altLabel?.Invoke(run) is { Length: > 0 } alt)
        {
            var key = Text(alt, true, 10); key.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont"); key.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(key, Dock.Right); row.Children.Add(key);
        }
        DockPanel.SetDock(status, Dock.Right); row.Children.Add(status);
        DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
        row.Children.Add(names);
        var border = new Border { Padding = new Thickness(30, 4, 10, 4), Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true };
        string where = run.Terminal.Available ? "Klick öffnet das Terminal" : "Klick öffnet die Makyx-Seite";
        border.ToolTip = $"{MakyxNames.TabName(run)} · {MakyxJson.StatusText(run.Status)}\n{run.Id} · {run.Title}\n{Detail(run)}\n{where}";
        System.Windows.Automation.AutomationProperties.SetName(border, $"{MakyxNames.RowText(run)}, {MakyxJson.StatusText(run.Status)}");
        border.MouseLeftButtonUp += (_, _) => ActivateRequested?.Invoke(run);
        border.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ActivateRequested?.Invoke(run); e.Handled = true; } };
        border.ContextMenu = BuildMenu(run);
        return border;
    }

    /// <summary>A finished run: number and title, below project, machine, duration and cost.</summary>
    private FrameworkElement RunRow(MakyxRun run)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = DotBrush(run.Status), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0) };
        var title = Text($"{run.Id}  {run.Title}", false, 12); title.FontWeight = FontWeights.SemiBold;
        var status = Text(MakyxJson.StatusText(run.Status), true, 11);
        var detail = Text(Detail(run), true, 11);
        var head = new DockPanel(); DockPanel.SetDock(status, Dock.Right); status.Margin = new Thickness(6, 0, 0, 0); head.Children.Add(status); head.Children.Add(title);
        var content = new StackPanel(); content.Children.Add(head); content.Children.Add(detail);
        var grid = new DockPanel(); DockPanel.SetDock(dot, Dock.Left); grid.Children.Add(dot); grid.Children.Add(content);
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5, 12, 6), Child = grid, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        border.ToolTip = $"{run.Id} · {run.Title} · {MakyxJson.StatusText(run.Status)}\n{Detail(run)}";
        System.Windows.Automation.AutomationProperties.SetName(border, $"{run.Id} {run.Title}, {MakyxJson.StatusText(run.Status)}, {Detail(run)}");
        border.MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke(run);
        border.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { OpenRequested?.Invoke(run); e.Handled = true; } };
        border.ContextMenu = BuildMenu(run);
        return border;
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
        // Steering (right "steuern"): only for active runs; free sessions end in their terminal.
        bool stoppable = MakyxControl.IsStoppable(run);
        var stop = new MenuItem { Header = "Anhalten …", IsEnabled = stoppable };
        stop.Click += (_, _) => StopRequested?.Invoke(run);
        var emergency = new MenuItem { Header = "Nothalt (ganz Makyx) …", IsEnabled = stoppable };
        emergency.Click += (_, _) => EmergencyRequested?.Invoke(run);
        menu.Items.Add(new Separator()); menu.Items.Add(stop); menu.Items.Add(emergency);
        return menu;
    }
}
