using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

public sealed class TmuxSessionDialog : Window
{
    private readonly Profile _profile;
    private readonly ListBox _list = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = "Aktualisieren" };
    private readonly Button _attach = new() { Content = "Session öffnen", IsEnabled = false, IsDefault = true };
    private readonly CancellationTokenSource _cancellation = new();
    public TmuxSessionInfo? SelectedSession { get; private set; }

    public TmuxSessionDialog(Profile profile)
    {
        _profile = profile;
        Style = (Style)FindResource(typeof(Window));
        Title = "tmux-Sessions · " + profile.Name;
        Width = 580; Height = 430; MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(22) };
        Content = panel;
        var title = new TextBlock { Text = "Vorhandene Sessions auf " + profile.Name,
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        DockPanel.SetDock(_status, Dock.Top); panel.Children.Add(_status);
        var footer = new StackPanel();
        footer.Children.Add(new TextBlock {
            Text = "Die ausgewählte Session wird hier angehängt. Eine bisherige tmux-Session läuft auf dem Server weiter.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightSlateGray,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var buttons = new WrapPanel();
        buttons.Children.Add(_attach); buttons.Children.Add(_refresh);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(_list);
        _list.SelectionChanged += (_, _) => _attach.IsEnabled = _list.SelectedItem is ListBoxItem;
        _attach.Click += (_, _) => Choose();
        _list.MouseDoubleClick += (_, _) => Choose();
        _refresh.Click += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _cancellation.Cancel();
    }

    private void Choose()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: TmuxSessionInfo session })
        {
            SelectedSession = session; DialogResult = true;
        }
    }

    private async Task RefreshAsync()
    {
        _refresh.IsEnabled = false; _attach.IsEnabled = false;
        _list.Items.Clear(); _status.Text = "Sessions werden abgefragt …";
        try
        {
            var sessions = await new TmuxSessionQuery().ListAsync(_profile, _cancellation.Token);
            if (_cancellation.IsCancellationRequested) return;
            foreach (var session in sessions)
            {
                var row = new StackPanel();
                row.Children.Add(new TextBlock { Text = session.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = session.Detail, Foreground = System.Windows.Media.Brushes.LightSlateGray, Margin = new Thickness(0, 5, 0, 0) });
                _list.Items.Add(new ListBoxItem { Content = row, Tag = session });
            }
            _status.Text = sessions.Count == 0 ? "Auf diesem Server laufen keine tmux-Sessions." : $"{sessions.Count} Session(s) gefunden";
            if (sessions.Count > 0) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _refresh.IsEnabled = true; }
    }
}
