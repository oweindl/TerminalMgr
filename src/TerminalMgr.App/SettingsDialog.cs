using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Application settings. Sections are added by the features that own them.</summary>
public sealed class SettingsDialog : Window
{
    private readonly AppSettings _original;
    private readonly TextBox _historyLimit;
    public AppSettings? Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _original = settings;
        Style = (Style)FindResource(typeof(Window));
        Title = "Einstellungen";
        Width = 560; Height = 420; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Clipboard", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Ungeheftete Einträge im Verlauf (1 bis 1000)" });
        _historyLimit = new TextBox { Text = settings.ClipHistoryLimit.ToString() };
        panel.Children.Add(_historyLimit);
        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(buttons);
        var save = new Button { Content = "Speichern", IsDefault = true };
        save.Click += (_, _) => {
            if (!int.TryParse(_historyLimit.Text.Trim(), out int limit) || limit is < 1 or > 1000)
            {
                MessageBox.Show(this, "Der Verlauf muss zwischen 1 und 1000 Einträge groß sein.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Result = _original with { ClipHistoryLimit = limit };
            DialogResult = true;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
    }
}
