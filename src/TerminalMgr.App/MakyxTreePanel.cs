using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// The MAKYX tree under the server list (note 15): machine › project › running instance. A click on a machine or project folds it,
/// a click on an instance opens its terminal; every level has its own right-click menu. Finished runs are not listed (they live in Makyx).
/// </summary>
public sealed class MakyxTreePanel : StackPanel
{
    public event Action<MakyxRun>? OpenRequested;
    public event Action<MakyxRun>? TerminalRequested;
    public event Action<MakyxRun>? CopyRequested;
    public event Action<MakyxRun>? BrowserRequested;
    /// <summary>"Anhalten …" (stops the run; the window asks first).</summary>
    public event Action<MakyxRun>? StopRequested;
    /// <summary>"Nothalt (ganz Makyx) …" (stops all of Makyx; the window asks first).</summary>
    public event Action<MakyxRun>? EmergencyRequested;
    /// <summary>"Session beenden …" on a free session (the window asks first).</summary>
    public event Action<MakyxRun>? EndRequested;
    /// <summary>"Antworten …" on an instance that waits for the user.</summary>
    public event Action<MakyxWaiting>? AnswerRequested;
    /// <summary>"Neue Session …" of a project; the machine is the one of the tree branch (null: Makyx chooses).</summary>
    public event Action<MakyxProject, MakyxMachine?>? NewSessionRequested;
    public event Action<MakyxProject>? RunRequested;
    public event Action<MakyxProject>? ProjectPageRequested;
    /// <summary>"Shell öffnen": a plain SSH connection with the profile of the machine (no Makyx).</summary>
    public event Action<MakyxMachine>? ShellRequested;
    /// <summary>The terminal entry is enabled when this returns null (a reason otherwise).</summary>
    public Func<MakyxRun, string?>? TerminalProblem { get; set; }
    /// <summary>"Shell öffnen" is enabled when this returns null (for example "kein SSH-Profil").</summary>
    public Func<MakyxMachine, string?>? ShellProblem { get; set; }

    /// <summary>Folding the user chose, by key of machine or project; without an entry machines are open and projects open when something runs.</summary>
    private readonly Dictionary<string, bool> _open = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MakyxTreeMachine> _tree = [];
    private IReadOnlyCollection<MakyxWaiting> _waiting = [];
    private string _empty = "";

    /// <summary>Dot colors as in Makyx: working cyan/blue, waiting amber, done green, failed red, blocked grey.</summary>
    public static Brush DotBrush(MakyxStatus status) => status switch
    {
        MakyxStatus.Working => new SolidColorBrush(Color.FromRgb(0x2B, 0xA4, 0xC8)),
        MakyxStatus.Waiting => new SolidColorBrush(Color.FromRgb(0xE0, 0xA1, 0x1B)),
        MakyxStatus.Done => new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0x6B)),
        MakyxStatus.Failed => new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x4F)),
        _ => new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0x9B))
    };

    public static string Duration(int? seconds) => seconds switch
    {
        null => "", < 60 => $"{seconds} s", < 3600 => $"{seconds / 60}:{seconds % 60:00} min", _ => $"{seconds / 3600} h {(seconds % 3600) / 60} min"
    };

    public static string Detail(MakyxRun run)
    {
        var parts = new List<string>();
        if (run.Project.Length > 0) parts.Add(run.Project);
        else if (run.Kind == "session") parts.Add("freie Session");
        if (run.Machine is { Length: > 0 }) parts.Add(run.Machine);
        if (Duration(run.DurationSeconds) is { Length: > 0 } d) parts.Add(d);
        if (run.CostUsd is { } c) parts.Add(c.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " USD");
        return string.Join(" · ", parts);
    }

    /// <summary>Is this machine or project unfolded? (The user's choice, else the default.)</summary>
    public bool IsOpen(string key, bool byDefault) => _open.TryGetValue(key, out bool open) ? open : byDefault;

    private static TextBlock Text(string text, bool muted, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Text2Brush" : "TextBrush");
        return block;
    }

    private static System.Windows.Shapes.Ellipse Dot(Brush? fill, double margin = 7)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, margin, 0), StrokeThickness = 1.5 };
        if (fill is null) dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "LineStrongBrush");
        else { dot.Fill = fill; dot.Stroke = fill; }
        return dot;
    }

    /// <summary>One row: indent, optional fold arrow, content on the left, a note on the right.</summary>
    private static Border Row(double indent, string? arrow, UIElement content, UIElement? right, string name, string tooltip)
    {
        var dock = new DockPanel { LastChildFill = true };
        var fold = Text(arrow ?? "", true, 10); fold.Width = 12;
        DockPanel.SetDock(fold, Dock.Left); dock.Children.Add(fold);
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
        dock.Children.Add(content);
        var border = new Border { Padding = new Thickness(8 + indent, 3, 10, 3), MinHeight = 24, Child = dock, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true, ToolTip = tooltip };
        System.Windows.Automation.AutomationProperties.SetName(border, name);
        return border;
    }

    private static void OnActivate(Border border, Action action)
    {
        border.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        border.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { action(); e.Handled = true; } };
    }

    private void Toggle(string key, bool current) { _open[key] = !current; Rebuild(); }

    public void Render(IReadOnlyList<MakyxTreeMachine> tree, IReadOnlyCollection<MakyxWaiting> waiting, string emptyText)
    {
        _tree = tree; _waiting = waiting; _empty = emptyText;
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (_tree.Count == 0)
        {
            var empty = Text(_empty, true, 11); empty.Margin = new Thickness(12, 4, 12, 8); empty.TextWrapping = TextWrapping.Wrap;
            Children.Add(empty);
            return;
        }
        foreach (var machine in _tree) AddMachine(machine);
    }

    private void AddMachine(MakyxTreeMachine node)
    {
        bool open = IsOpen(node.Key, node.Connected || node.ActiveCount > 0);
        var name = Text(node.Name, !node.Connected && node.Machine is not null, 12); name.FontWeight = FontWeights.SemiBold;
        var content = new DockPanel();
        if (node.Machine is not null) { var dot = Dot(node.Connected ? DotBrush(MakyxStatus.Done) : null); DockPanel.SetDock(dot, Dock.Left); content.Children.Add(dot); }
        content.Children.Add(name);
        string note = node.Machine is null ? "" : !node.Connected ? "getrennt" : node.ActiveCount == 1 ? "1 aktiv" : node.ActiveCount + " aktiv";
        var right = Text(note, true, 11);
        string tooltip = node.Machine is null ? "Projekte und Läufe, für die Makyx keinen Rechner meldet"
            : $"{node.Name} · {(node.Connected ? "verbunden" : "getrennt")} · {node.ActiveCount} laufende Instanz(en); jede belegt einen Session-Platz des Runners";
        var row = Row(0, open ? "⌄" : "›", content, right, $"Rechner {node.Name}, {note}", tooltip);
        row.Margin = new Thickness(0, Children.Count == 0 ? 2 : 6, 0, 0);
        OnActivate(row, () => Toggle(node.Key, open));
        if (node.Machine is { } machine) row.ContextMenu = MachineMenu(machine, node);
        Children.Add(row);
        if (!open) return;
        if (node.Machine is { } m && node.Connected)
        {
            string? problem = ShellProblem?.Invoke(m);
            var shellText = Text("Shell öffnen" + (problem is null ? "" : " – " + problem), true, 11);
            var shell = Row(16, null, shellText, null, "Shell auf " + node.Name + " öffnen", "Normale SSH-Verbindung mit dem Profil des Rechners (dein tmux, ohne Makyx)");
            shell.IsEnabled = problem is null;
            OnActivate(shell, () => ShellRequested?.Invoke(m));
            Children.Add(shell);
        }
        foreach (var project in node.Projects) AddProject(node, project);
    }

    private void AddProject(MakyxTreeMachine machine, MakyxTreeProject node)
    {
        bool running = node.Active.Count > 0;
        bool open = IsOpen(node.Key, running);
        var name = Text(node.Name, !running, 12);
        string note = (running ? (node.Active.Count == 1 ? "1 läuft" : node.Active.Count + " laufen") : "")
            + (node.GatesOpen > 0 ? (running ? " · " : "") + (node.GatesOpen == 1 ? "1 Gate" : node.GatesOpen + " Gates") : "");
        var right = Text(note, true, 11);
        if (node.GatesOpen > 0) right.Foreground = DotBrush(MakyxStatus.Waiting);
        var row = Row(16, open ? "⌄" : "›", name, right, $"Projekt {node.Name}, {note}", node.Name + (note.Length > 0 ? " · " + note : ""));
        OnActivate(row, () => Toggle(node.Key, open));
        if (node.Project is { } p) row.ContextMenu = ProjectMenu(p, machine.Machine);
        Children.Add(row);
        if (!open) return;
        foreach (var run in node.Active) AddInstance(run);
        if (node.Project is { } project)
        {
            var add = Text("+ Neue Session", false, 11);
            add.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var addRow = Row(32, null, add, null, "Neue Session in " + node.Name, "Startet einen Agenten in " + node.Name + (machine.Machine is null ? "" : " auf " + machine.Name));
            OnActivate(addRow, () => NewSessionRequested?.Invoke(project, machine.Machine));
            Children.Add(addRow);
        }
    }

    private void AddInstance(MakyxRun run)
    {
        var waiting = MakyxTree.WaitingFor(run, _waiting);
        var content = new DockPanel();
        var dot = Dot(DotBrush(run.Status)); DockPanel.SetDock(dot, Dock.Left); content.Children.Add(dot);
        content.Children.Add(Text(MakyxTree.InstanceName(run), false, 12));
        string state = waiting is not null ? "wartet auf dich" : MakyxJson.StatusText(run.Status);
        var right = Text(state, run.Status != MakyxStatus.Waiting, 11);
        if (run.Status == MakyxStatus.Waiting) right.Foreground = DotBrush(MakyxStatus.Waiting);
        string? problem = TerminalProblemOf(run);
        string tooltip = $"{run.Id} · {run.Title} · {MakyxJson.StatusText(run.Status)}\n{Detail(run)}\n" + (problem is null ? "Klick öffnet das Terminal" : "Klick öffnet die Makyx-Seite (" + problem + ")");
        var row = Row(32, null, content, right, $"{MakyxTree.InstanceName(run)}, {state}", tooltip);
        OnActivate(row, () => { if (problem is null) TerminalRequested?.Invoke(run); else OpenRequested?.Invoke(run); });
        row.ContextMenu = InstanceMenu(run, waiting, problem);
        Children.Add(row);
    }

    private string? TerminalProblemOf(MakyxRun run) => run.Terminal.Available ? TerminalProblem?.Invoke(run)
        : "kein Terminal" + (run.Terminal.Reason == "strukturiert" ? " (strukturierte Session)" : run.Terminal.Reason == "kein_tmux" ? " (Rechner meldet kein tmux)" : "");

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private ContextMenu MachineMenu(MakyxMachine machine, MakyxTreeMachine node)
    {
        var menu = new ContextMenu();
        string? problem = ShellProblem?.Invoke(machine);
        menu.Items.Add(Item("Shell öffnen" + (problem is null ? "" : " – " + problem), () => ShellRequested?.Invoke(machine), problem is null && node.Connected));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = $"{node.ActiveCount} laufende Instanz(en) · {(node.Connected ? "verbunden" : "getrennt")}", IsEnabled = false });
        return menu;
    }

    private ContextMenu ProjectMenu(MakyxProject project, MakyxMachine? machine)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Neue Session …", () => NewSessionRequested?.Invoke(project, machine)));
        menu.Items.Add(Item("Lauf starten …", () => RunRequested?.Invoke(project)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Projektseite öffnen", () => ProjectPageRequested?.Invoke(project), project.Link.Length > 0));
        return menu;
    }

    private ContextMenu InstanceMenu(MakyxRun run, MakyxWaiting? waiting, string? problem)
    {
        var menu = new ContextMenu();
        var terminal = Item("Im Terminal öffnen" + (problem is null ? "" : " – " + problem), () => TerminalRequested?.Invoke(run), problem is null);
        terminal.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(terminal);
        if (waiting is not null) menu.Items.Add(Item("Antworten …", () => AnswerRequested?.Invoke(waiting)));
        menu.Items.Add(Item("In Makyx öffnen", () => OpenRequested?.Invoke(run), run.Link.Length > 0));
        menu.Items.Add(Item("Im Browser öffnen", () => BrowserRequested?.Invoke(run), run.Link.Length > 0));
        menu.Items.Add(Item("Link kopieren", () => CopyRequested?.Invoke(run), run.Link.Length > 0));
        // Steering (right "steuern"): runs and jobs are stopped through Makyx; a free session has no such call, TerminalMgr ends its tmux window.
        bool stoppable = MakyxControl.IsStoppable(run);
        menu.Items.Add(new Separator());
        if (run.Kind == "session")
        {
            bool endable = MakyxControl.IsEndable(run);
            menu.Items.Add(Item(endable ? "Session beenden …" : "Session beenden – " + (run.IsActive ? "kein tmux-Fenster" : "schon beendet"), () => EndRequested?.Invoke(run), endable));
        }
        else menu.Items.Add(Item(stoppable ? "Anhalten …" : "Anhalten – schon beendet", () => StopRequested?.Invoke(run), stoppable));
        menu.Items.Add(Item("Nothalt (ganz Makyx) …", () => EmergencyRequested?.Invoke(run), stoppable));
        return menu;
    }
}
