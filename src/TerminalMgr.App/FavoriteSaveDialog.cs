using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Small dialog behind Ctrl+D in the command palette: name and (optional) shortcut for a new favorite.</summary>
public sealed class FavoriteSaveDialog : Window
{
    private readonly Favorite _favorite;
    private readonly TextBox _name, _shortcut;
    public Favorite? Result { get; private set; }

    public FavoriteSaveDialog(Favorite favorite, string serverName)
    {
        _favorite = favorite;
        Style = (Style)FindResource(typeof(Window));
        Title = "Als Favorit speichern";
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = $"{serverName} › {favorite.SessionName} › {favorite.WindowName}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Name" });
        _name = new TextBox { Text = favorite.Name, Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = "Tastenkürzel (Feld anklicken und Kombination drücken; Entf löscht)" });
        _shortcut = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 4, 0, 4) };
        _shortcut.PreviewKeyDown += CaptureShortcut;
        panel.Children.Add(_shortcut);
        panel.Children.Add(new TextBlock { Text = "Ohne Kürzel ist der Favorit nur über die Befehlspalette erreichbar. Gebunden wird an Server, Session-Name und Fenster-Name.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, FontSize = 11 });
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Speichern", IsDefault = true };
        ok.Click += (_, _) => { Result = _favorite with { Name = _name.Text.Trim(), Shortcut = _shortcut.Text }; DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        panel.Children.Add(buttons);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None) { _shortcut.Text = ""; return; }
        var pressed = ShortcutKeys.FromEvent(e);
        if (pressed is null) return;
        if (!Shortcut.TryParse(pressed.ToString(), out var parsed))
        { MessageBox.Show(this, "Bitte mit Ctrl oder Alt kombinieren (nur F1 bis F12 gehen allein).", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _shortcut.Text = parsed.ToString();
    }
}
