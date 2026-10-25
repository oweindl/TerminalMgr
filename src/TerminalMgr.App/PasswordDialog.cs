using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalMgr.App;

/// <summary>Asks for the password of a protected configuration file.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _password = new() { Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public string Password => _password.Password;

    public PasswordDialog(string message, string? error = null)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Passwort";
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_password);
        if (error is not null) { _error.Text = error; _error.Visibility = Visibility.Visible; }
        panel.Children.Add(_error);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);
        Loaded += (_, _) => _password.Focus();
    }
}
