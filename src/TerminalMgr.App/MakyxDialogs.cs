using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// "Neue Claude-Session" (note 16, mockup B): project with search, optional short name, kind (interactive or run with points), machine with free
/// memory, working folder, agent and model, first message. One step instead of four menu levels; Enter (Ctrl+Enter in the text) starts.
/// </summary>
public sealed class MakyxSessionDialog : Window
{
    private readonly TextBox _search = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ListBox _projects = new() { Height = 150, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 2) };
    private readonly TextBlock _nameHint = new() { FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
    private readonly RadioButton _interactive = new() { Content = "Interaktiv", IsChecked = true, Margin = new Thickness(0, 4, 16, 10), GroupName = "art" };
    private readonly RadioButton _run = new() { Content = "Lauf mit Auftrag", Margin = new Thickness(0, 4, 0, 10), GroupName = "art" };
    // Not editable: the dark ComboBox template of the app has no text box, an editable list could not be chosen from.
    private readonly ComboBox _machine = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly RadioButton _worktree = new() { Content = "Neuer Git-Worktree", IsChecked = true, Margin = new Thickness(0, 4, 16, 10), GroupName = "ordner" };
    private readonly RadioButton _main = new() { Content = "Hauptordner des Projekts", Margin = new Thickness(0, 4, 0, 10), GroupName = "ordner" };
    private readonly ComboBox _agent = new() { Margin = new Thickness(0, 4, 8, 10), MinWidth = 110 };
    private readonly TextBox _model = new() { Margin = new Thickness(0, 4, 0, 10), MinWidth = 220 };
    private readonly TextBlock _messageLabel = new() { Text = "Erste Nachricht (optional, leer = die Session wartet auf Eingabe)" };
    private readonly TextBox _message = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 80, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 12 };
    private readonly Button _ok = new() { Content = "Session starten", IsDefault = true };
    private readonly IReadOnlyList<MakyxProject> _all;
    private readonly IReadOnlyList<MakyxMachine> _machines;

    public MakyxProject? Project => _projects.SelectedItem is ListBoxItem { Tag: MakyxProject p } ? p : null;
    public string? ShortName => MakyxNames.Normalize(_name.Text);
    public bool IsRun => _run.IsChecked == true;
    /// <summary>The chosen machine as Makyx knows it; null lets Makyx choose (no machine reported).</summary>
    public string? Machine => _machine.SelectedIndex >= 0 && _machine.SelectedIndex < _machines.Count ? MakyxControl.MachineValue(_machines[_machine.SelectedIndex]) : null;
    public MakyxFolder Folder => _main.IsChecked == true ? MakyxFolder.Main : MakyxFolder.Worktree;
    public string Agent => _agent.SelectedItem as string ?? MakyxControl.Agents[0];
    public string? Model => string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim();
    public string? Message => string.IsNullOrWhiteSpace(_message.Text) ? null : _message.Text.Trim();
    public IReadOnlyList<string> Items => MakyxControl.RunItems(_message.Text);

    public MakyxSessionDialog(IReadOnlyList<MakyxProject> projects, MakyxProject? preselected, IReadOnlyList<MakyxMachine> machines)
    {
        _all = MakyxControl.SortProjects(projects);
        _machines = machines;
        Style = (Style)FindResource(typeof(Window));
        Title = "Neue Claude-Session";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var keys = new TextBlock { Text = "Ctrl+N", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; keys.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        DockPanel.SetDock(keys, Dock.Right); head.Children.Add(keys);
        head.Children.Add(new TextBlock { Text = "Neue Claude-Session", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(head);

        panel.Children.Add(Label("Projekt"));
        panel.Children.Add(_search);
        panel.Children.Add(_projects);
        panel.Children.Add(Label("Name (optional, z. B. ingest-api; leer = Makyx vergibt ihn)"));
        panel.Children.Add(_name);
        _nameHint.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        panel.Children.Add(_nameHint);

        var art = new WrapPanel(); art.Children.Add(_interactive); art.Children.Add(_run);
        panel.Children.Add(Label("Art")); panel.Children.Add(art);

        panel.Children.Add(Label("Ziel-Host"));
        foreach (var m in machines) _machine.Items.Add(MakyxControl.MachineLabel(m));
        if (machines.Count == 0) { _machine.Items.Add("Makyx wählt den Rechner"); _machine.IsEnabled = false; }
        panel.Children.Add(_machine);

        var folder = new WrapPanel(); folder.Children.Add(_worktree); folder.Children.Add(_main);
        panel.Children.Add(Label("Arbeitsordner")); panel.Children.Add(folder);

        foreach (string a in MakyxControl.Agents) _agent.Items.Add(a);
        _agent.SelectedIndex = 0;
        var runtime = new WrapPanel(); runtime.Children.Add(_agent); runtime.Children.Add(_model);
        panel.Children.Add(Label("Agent und Modell")); panel.Children.Add(runtime);

        panel.Children.Add(_messageLabel);
        panel.Children.Add(_message);
        _result.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        panel.Children.Add(_result);

        var buttons = new DockPanel();
        var hint = new TextBlock { Text = "Enter startet · Esc bricht ab", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; hint.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        var cancel = new Button { Content = "Abbrechen", IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(_ok, Dock.Right);
        buttons.Children.Add(cancel); buttons.Children.Add(_ok); buttons.Children.Add(hint);
        panel.Children.Add(buttons);
        Content = panel;

        _ok.Click += (_, _) => { if (Valid()) DialogResult = true; };
        _search.TextChanged += (_, _) => FillProjects(Project);
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _projects.Items.Count > 0) { _projects.SelectedIndex = Math.Min(_projects.Items.Count - 1, _projects.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up && _projects.Items.Count > 0) { _projects.SelectedIndex = Math.Max(0, _projects.SelectedIndex - 1); e.Handled = true; }
        };
        _projects.SelectionChanged += (_, _) => ProjectChanged();
        _name.TextChanged += (_, _) => Update();
        _interactive.Checked += (_, _) => Update(); _run.Checked += (_, _) => Update();
        _machine.SelectionChanged += (_, _) => Update();
        _worktree.Checked += (_, _) => Update(); _main.Checked += (_, _) => Update();
        _message.TextChanged += (_, _) => Update();
        _message.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (Valid()) DialogResult = true; } };
        FillProjects(preselected);
        foreach (var (control, label) in new (Control, string)[] { (_search, "Projekt suchen"), (_projects, "Projekte"), (_name, "Name der Session"), (_machine, "Ziel-Host"), (_agent, "Agent"), (_model, "Modell"), (_message, "Erste Nachricht") })
            System.Windows.Automation.AutomationProperties.SetName(control, label);
        Loaded += (_, _) => { if (preselected is not null) _name.Focus(); else _search.Focus(); };
    }

    private static TextBlock Label(string text) => new() { Text = text };

    private void FillProjects(MakyxProject? keep)
    {
        string query = _search.Text.Trim();
        var now = DateTimeOffset.Now;
        _projects.Items.Clear();
        foreach (var project in _all.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var name = new TextBlock { Text = project.Name, FontWeight = FontWeights.SemiBold };
            var detail = new TextBlock { Text = MakyxControl.ProjectDetail(project, now), FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            var row = new DockPanel(); DockPanel.SetDock(detail, Dock.Right); row.Children.Add(detail); row.Children.Add(name);
            var item = new ListBoxItem { Content = row, Tag = project, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(item, project.Name + ", " + MakyxControl.ProjectDetail(project, now));
            _projects.Items.Add(item);
        }
        int index = keep is null ? -1 : _projects.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag is MakyxProject p && p.Id == keep.Id);
        _projects.SelectedIndex = index >= 0 ? index : _projects.Items.Count > 0 ? 0 : -1;
        if (_projects.SelectedItem is not null) _projects.ScrollIntoView(_projects.SelectedItem);
        ProjectChanged();
    }

    private void ProjectChanged()
    {
        var project = Project;
        if (_machines.Count > 0) _machine.SelectedIndex = Math.Max(0, MakyxControl.PreferredMachine(_machines, project?.Machine));
        else _machine.SelectedIndex = 0;
        _model.Text = "";
        _model.ToolTip = project?.DefaultModel is { } m ? "Leer = " + m + " (Vorgabe des Projekts)" : "Leer = Vorgabe von Makyx";
        Update();
    }

    private void Update()
    {
        string? shortName = ShortName;
        _nameHint.Text = _name.Text.Trim().Length == 0 ? "" : shortName is null ? "Kein gültiger Name: Buchstaben, Ziffern und - verwenden." : shortName == _name.Text.Trim() ? "" : "Wird zu „" + shortName + "“.";
        _messageLabel.Text = IsRun ? "Punkte des Laufs, einer je Zeile (Backlog-Kennung oder Text eines neuen Punkts)" : "Erste Nachricht (optional, leer = die Session wartet auf Eingabe)";
        _ok.Content = IsRun ? "Lauf starten" : "Session starten";
        _worktree.IsEnabled = _main.IsEnabled = !IsRun;
        var project = Project;
        if (project is null) { _result.Text = "Bitte ein Projekt wählen."; return; }
        _result.Text = IsRun
            ? $"Ergebnis: Lauf in {project.Name} mit {Items.Count} Punkt(en); er erscheint unter LÄUFE."
            : $"Ergebnis: Tab „{project.Name} · {shortName ?? "(Name von Makyx)"}“" + (Machine is { } m ? " · " + m : "") + " · " + (Folder == MakyxFolder.Main ? "Hauptordner" : "neuer Worktree");
    }

    private bool Valid()
    {
        if (Project is null) { _result.Text = "Bitte ein Projekt wählen."; _search.Focus(); return false; }
        if (_name.Text.Trim().Length > 0 && ShortName is null) { _name.Focus(); return false; }
        if (IsRun && Items.Count == 0) { _result.Text = "Bitte mindestens einen Punkt angeben (eine Zeile je Punkt)."; _message.Focus(); return false; }
        if (IsRun && Items.Count > MakyxControl.MaxRunItems) { _result.Text = $"Höchstens {MakyxControl.MaxRunItems} Punkte je Lauf."; return false; }
        return true;
    }
}

/// <summary>A text for Makyx: the answer to a question or gate, or the points of a new run (one per line).</summary>
public sealed class MakyxTextDialog : Window
{
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 140, Margin = new Thickness(0, 6, 0, 12) };
    public string Text => _text.Text;

    public MakyxTextDialog(string title, string prompt, string okText, bool required)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = title;
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_text);
        var buttons = new WrapPanel();
        var ok = new Button { Content = okText, IsEnabled = !required };
        ok.Click += (_, _) => DialogResult = true;
        if (required) _text.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(_text, prompt);
        Loaded += (_, _) => _text.Focus();
    }
}
