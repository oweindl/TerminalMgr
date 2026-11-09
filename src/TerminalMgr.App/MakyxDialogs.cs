using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>"Neue Session" for a Makyx project: machine, runtime (agent) and optional model. The session starts in terminal mode.</summary>
public sealed class MakyxSessionDialog : Window
{
    // Not editable: the dark ComboBox template of the app has no text box, an editable list could not be chosen from.
    private readonly ComboBox _machine = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly ComboBox _agent = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _model = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly IReadOnlyList<MakyxMachine> _machines;
    /// <summary>The chosen machine as Makyx knows it; null lets Makyx choose (no machine reported).</summary>
    public string? Machine => _machine.SelectedIndex >= 0 && _machine.SelectedIndex < _machines.Count ? MakyxControl.MachineValue(_machines[_machine.SelectedIndex]) : null;
    public string Agent => _agent.SelectedItem as string ?? MakyxControl.Agents[0];
    public string? Model => string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim();

    public MakyxSessionDialog(MakyxProject project, IReadOnlyList<MakyxMachine> machines)
    {
        _machines = machines;
        Style = (Style)FindResource(typeof(Window));
        Title = "Neue Session · " + project.Name;
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Neue Session in " + project.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Rechner" });
        foreach (var m in machines) _machine.Items.Add(MakyxControl.MachineLabel(m));
        if (machines.Count == 0) { _machine.Items.Add("Makyx wählt den Rechner"); _machine.IsEnabled = false; }
        _machine.SelectedIndex = Math.Max(0, MakyxControl.PreferredMachine(machines, project.Machine));
        panel.Children.Add(_machine);
        panel.Children.Add(new TextBlock { Text = "Agent" });
        foreach (string a in MakyxControl.Agents) _agent.Items.Add(a);
        _agent.SelectedIndex = 0;
        panel.Children.Add(_agent);
        panel.Children.Add(new TextBlock { Text = "Modell (leer = Vorgabe von Makyx)" });
        panel.Children.Add(_model);
        panel.Children.Add(new TextBlock { Text = "Die Session startet im Terminal-Modus; TerminalMgr öffnet ihr tmux-Fenster, sobald Makyx es meldet.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new WrapPanel();
        var ok = new Button { Content = "Session starten", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(_machine, "Rechner");
        System.Windows.Automation.AutomationProperties.SetName(_agent, "Agent");
        System.Windows.Automation.AutomationProperties.SetName(_model, "Modell");
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
