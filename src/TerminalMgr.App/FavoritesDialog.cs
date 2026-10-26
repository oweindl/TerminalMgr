using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// Favorites: tmux windows with a name and an own shortcut, bound to server, session name and window name.
/// New ones come from the windows used last (the history) or are typed in; the shortcut field records the pressed keys.
/// </summary>
public sealed class FavoritesDialog : Window
{
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly IReadOnlyList<NavigationPosition> _recent;
    private readonly StackPanel _rows = new();
    private readonly List<Row> _list = [];
    public List<Favorite>? Result { get; private set; }

    private sealed record Row(Favorite Original, TextBox Name, ComboBox Server, TextBox Session, TextBox Window, TextBox Shortcut, UIElement Visual);

    public FavoritesDialog(IEnumerable<Favorite> favorites, IReadOnlyList<Profile> profiles, IReadOnlyList<NavigationPosition> recent)
    {
        _profiles = profiles;
        _recent = recent;
        Style = (Style)FindResource(typeof(Window));
        Title = "Favoriten";
        Width = 860; Height = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var ok = new Button { Content = "Übernehmen", IsDefault = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        root.Children.Add(buttons);

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "Ein Favorit springt per Tastenkürzel in ein tmux-Fenster, auch auf einem getrennten Server (er wird dann verbunden). " +
            "Gebunden wird an Server, Session-Name und Fenster-Name, nicht an tmux-IDs; bei gleichnamigen Fenstern gilt das erste.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        var add = new WrapPanel();
        var recentBox = new ComboBox { MinWidth = 330, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0) };
        recentBox.ItemsSource = _recent.Select(p => $"{ServerName(p.ProfileId)} › {p.SessionName} › {p.WindowName}").ToList();
        recentBox.ToolTip = "Die zuletzt benutzten tmux-Fenster (seit dem Start der App)";
        if (_recent.Count > 0) recentBox.SelectedIndex = 0;
        var addRecent = new Button { Content = "Zuletzt benutztes Fenster hinzufügen", IsEnabled = _recent.Count > 0 };
        addRecent.Click += (_, _) => { if (recentBox.SelectedIndex >= 0) AddRow(new Favorite { ProfileId = _recent[recentBox.SelectedIndex].ProfileId, SessionName = _recent[recentBox.SelectedIndex].SessionName, WindowName = _recent[recentBox.SelectedIndex].WindowName }); };
        var addEmpty = new Button { Content = "Leeren Favoriten hinzufügen" };
        addEmpty.Click += (_, _) => AddRow(new Favorite { ProfileId = profiles.FirstOrDefault()?.Id ?? "" });
        add.Children.Add(recentBox); add.Children.Add(addRecent); add.Children.Add(addEmpty);
        header.Children.Add(add);
        header.Children.Add(new TextBlock { Text = "Name · Server · Session · Fenster · Tastenkürzel (Feld anklicken und Kombination drücken)", Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var favorite in favorites) AddRow(favorite);
    }

    private string ServerName(string profileId) => _profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? "(Server fehlt)";

    private void AddRow(Favorite favorite)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        foreach (double width in new[] { 150, 130, 120, 140, 150 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBox Box(string text, int column, string tip)
        {
            var box = new TextBox { Text = text, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            Grid.SetColumn(box, column);
            grid.Children.Add(box);
            return box;
        }
        var name = Box(favorite.Name, 0, "Anzeigename (leer: Session › Fenster)");
        var server = new ComboBox { ItemsSource = _profiles.Select(p => p.Name).ToList(), Margin = new Thickness(0, 0, 6, 0) };
        server.SelectedIndex = Math.Max(0, _profiles.ToList().FindIndex(p => p.Id == favorite.ProfileId));
        Grid.SetColumn(server, 1);
        grid.Children.Add(server);
        var session = Box(favorite.SessionName, 2, "Name der tmux-Session");
        var window = Box(favorite.WindowName, 3, "Name des tmux-Fensters");
        var shortcut = Box(favorite.Shortcut, 4, "Leer: nur über die Befehlspalette erreichbar");
        shortcut.IsReadOnly = true; shortcut.IsReadOnlyCaretVisible = false;
        shortcut.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            if (e.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None) { shortcut.Text = ""; return; }
            var pressed = ShortcutKeys.FromEvent(e);
            if (pressed is null) return;
            if (!Shortcut.TryParse(pressed.ToString(), out var parsed))
            { MessageBox.Show(this, "Bitte mit Ctrl oder Alt kombinieren (nur F1 bis F12 gehen allein).", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            shortcut.Text = parsed.ToString();
        };
        var remove = new Button { Content = "×", ToolTip = "Favorit entfernen" };
        Grid.SetColumn(remove, 5);
        grid.Children.Add(remove);
        var row = new Row(favorite, name, server, session, window, shortcut, grid);
        remove.Click += (_, _) => { _list.Remove(row); _rows.Children.Remove(grid); };
        _list.Add(row);
        _rows.Children.Add(grid);
    }

    private void Accept()
    {
        var result = new List<Favorite>();
        foreach (var row in _list)
        {
            if (row.Session.Text.Trim().Length == 0 || row.Window.Text.Trim().Length == 0)
            { MessageBox.Show(this, "Jeder Favorit braucht einen Session- und einen Fenster-Namen.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (_profiles.Count == 0)
            { MessageBox.Show(this, "Es gibt noch keinen Server.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            result.Add(row.Original with {
                Name = row.Name.Text.Trim(), ProfileId = _profiles[Math.Max(0, row.Server.SelectedIndex)].Id,
                SessionName = row.Session.Text.Trim(), WindowName = row.Window.Text.Trim(), Shortcut = row.Shortcut.Text
            });
        }
        Result = result;
        DialogResult = true;
    }
}
