using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Asks for the name of a new tmux session or window. A refused name stays in the dialog with the reason, nothing is started.</summary>
public sealed class TmuxNameDialog : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 6, 0, 4), MaxLength = TmuxNaming.MaxLength };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 0, 0, 8) };
    private readonly bool _optional, _anyText;
    /// <summary>The chosen name, trimmed; empty only when the name is optional.</summary>
    public string Value => _name.Text.Trim();

    /// <param name="anyText">The new name of an existing window: any text without control characters (the name tmux gave it may have spaces), not the strict session rule.</param>
    public TmuxNameDialog(string title, string prompt, string initial, bool optional, string okText, bool anyText = false)
    {
        _optional = optional;
        _anyText = anyText;
        Style = (Style)FindResource(typeof(Window));
        Title = title;
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        _name.Text = initial;
        _name.TextChanged += (_, _) => _error.Text = "";
        panel.Children.Add(_name);
        panel.Children.Add(_error);
        var buttons = new WrapPanel();
        var ok = new Button { Content = okText, IsDefault = true };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(_name, prompt);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    /// <summary>Checks the name; the dialog only closes with a valid one.</summary>
    public bool Confirm()
    {
        string? problem = _anyText ? TmuxNaming.WindowNameProblem(_name.Text) : TmuxNaming.Problem(_name.Text, _optional);
        if (problem is not null) { _error.Text = problem; return false; }
        DialogResult = true;
        return true;
    }

    /// <summary>For tests: sets the text as if typed.</summary>
    public void SetText(string text) => _name.Text = text;
    public string ErrorText => _error.Text;
}
