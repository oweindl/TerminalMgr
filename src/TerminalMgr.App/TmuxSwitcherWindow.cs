using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

public enum TmuxSwitcherAction { Show, NewTab, NewWindow, Rename }

/// <summary>
/// "Fenster wechseln" (note 16, mockup C): every tmux window of a server, grouped by session; Makyx windows as "Makyx · Projekt" with status,
/// the viewing sessions (ansicht-*, tm-*) left out. Search field on top; ↑↓ choose, Enter shows in this tab, Ctrl+Enter in a new tab,
/// N new window, R rename (N and R only while the list has the focus, so they can be typed into the search).
/// </summary>
public sealed class TmuxSwitcherWindow : Window
{
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _list = new() { MaxHeight = 460, MinHeight = 120 };
    private readonly IReadOnlyList<TmuxSwitcherEntry> _all;
    public TmuxSwitcherEntry? Chosen { get; private set; }
    public TmuxSwitcherAction Action { get; private set; }
    public string? NewName { get; private set; }

    public TmuxSwitcherWindow(string server, IReadOnlyList<TmuxSwitcherEntry> entries, string shortcut)
    {
        _all = entries;
        Style = (Style)FindResource(typeof(Window));
        Title = "Fenster wechseln · " + server;
        Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var keys = Muted(shortcut, 11); DockPanel.SetDock(keys, Dock.Right); head.Children.Add(keys);
        var title = new TextBlock { Text = "Fenster wechseln", FontSize = 16, FontWeight = FontWeights.SemiBold };
        head.Children.Add(title);
        var where = Muted("  " + server, 13); where.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(where);
        panel.Children.Add(head);
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        bool hidden = entries.Any(e => e.Socket is not null);
        var footer = Muted("↑↓ wählen · Enter hier zeigen · Ctrl+Enter neuer Tab · N neues Fenster · R umbenennen · Esc schließen" + (hidden ? "\nHilfssessions ansicht-* und tm-* ausgeblendet" : ""), 11);
        footer.Margin = new Thickness(0, 10, 0, 0); footer.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(footer);
        Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Fenster suchen");
        System.Windows.Automation.AutomationProperties.SetName(_list, "tmux-Fenster");
        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += SearchKeys;
        _list.PreviewKeyDown += ListKeys;
        _list.MouseDoubleClick += (_, _) => Finish(TmuxSwitcherAction.Show);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        Fill();
        Loaded += (_, _) => _search.Focus();
    }

    private static TextBlock Muted(string text, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        return block;
    }

    private void Fill()
    {
        _list.Items.Clear();
        string? group = null;
        int select = -1;
        foreach (var entry in TmuxSwitcher.Filter(_all, _search.Text))
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                var header = Muted(entry.Group.ToUpperInvariant(), 11); header.FontWeight = FontWeights.SemiBold; header.Margin = new Thickness(2, _list.Items.Count == 0 ? 2 : 10, 0, 2);
                _list.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false });
            }
            _list.Items.Add(Row(entry));
            if (entry.Here && select < 0) select = _list.Items.Count - 1;
        }
        if (select < 0) select = _list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag is TmuxSwitcherEntry);
        _list.SelectedIndex = select;
        if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
    }

    private static ListBoxItem Row(TmuxSwitcherEntry entry)
    {
        var index = Muted(entry.Window.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), 13); index.Width = 22;
        index.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        Brush? statusBrush = entry.Status switch
        {
            "wartet" => MakyxRunsPanel.DotBrush(MakyxStatus.Waiting), "arbeitet" => MakyxRunsPanel.DotBrush(MakyxStatus.Working), _ => null
        };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.5 };
        if (statusBrush is not null) dot.Fill = statusBrush;
        else if (entry.Window.Active) dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ConnectedBrush");
        else dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "LineStrongBrush");
        var name = new TextBlock { Text = entry.Window.Name, FontWeight = entry.Here ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        string extra = entry.Title.Length > 0 ? entry.Title : entry.Window.Command.Length > 0 && entry.Window.Command != entry.Window.Name ? entry.Window.Command : "";
        var detail = Muted(extra.Length > 0 ? " · " + extra : "", 13); detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        if (entry.Here) { var here = new TextBlock { Text = "dieser Tab", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; here.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); DockPanel.SetDock(here, Dock.Right); row.Children.Add(here); }
        if (entry.Status.Length > 0) { var status = new TextBlock { Text = entry.Status, FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = statusBrush ?? Brushes.Gray }; DockPanel.SetDock(status, Dock.Right); row.Children.Add(status); }
        DockPanel.SetDock(index, Dock.Left); row.Children.Add(index);
        DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
        DockPanel.SetDock(name, Dock.Left); row.Children.Add(name);
        row.Children.Add(detail);
        var item = new ListBoxItem { Content = row, Tag = entry, Padding = new Thickness(6, 5, 6, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(item, $"{entry.Group}, Fenster {entry.Window.Index}: {entry.Window.Name}" + (entry.Status.Length > 0 ? ", " + entry.Status : "") + (entry.Here ? ", dieser Tab" : ""));
        return item;
    }

    private void Move(int delta)
    {
        int count = _list.Items.Count;
        for (int i = _list.SelectedIndex + delta; i >= 0 && i < count; i += delta)
            if (_list.Items[i] is ListBoxItem { Tag: TmuxSwitcherEntry }) { _list.SelectedIndex = i; _list.ScrollIntoView(_list.Items[i]); return; }
    }

    private void SearchKeys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter: Finish(Keyboard.Modifiers == ModifierKeys.Control ? TmuxSwitcherAction.NewTab : TmuxSwitcherAction.Show); e.Handled = true; break;
        }
    }

    private void ListKeys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter: Finish(Keyboard.Modifiers == ModifierKeys.Control ? TmuxSwitcherAction.NewTab : TmuxSwitcherAction.Show); e.Handled = true; break;
            case Key.N when Keyboard.Modifiers == ModifierKeys.None: Finish(TmuxSwitcherAction.NewWindow); e.Handled = true; break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.None: Rename(); e.Handled = true; break;
        }
    }

    private void Rename()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: TmuxSwitcherEntry entry }) return;
        bool makyx = entry.Socket is not null && entry.Status.Length > 0;
        var dialog = new MakyxTextDialog("Umbenennen · " + entry.Window.Name,
            makyx ? "Neuer Name der Session (Buchstaben, Ziffern und -, höchstens 20 Zeichen); Makyx benennt das tmux-Fenster mit um." : "Neuer Name des tmux-Fensters.",
            "Umbenennen", required: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        string text = dialog.Text.Trim();
        if (text.Length == 0 || text.Length > 60 || text.Any(char.IsControl)) return;
        if (makyx && MakyxNames.Normalize(text) is null) return;
        NewName = text;
        Finish(TmuxSwitcherAction.Rename);
    }

    private void Finish(TmuxSwitcherAction action)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: TmuxSwitcherEntry entry }) return;
        Chosen = entry; Action = action;
        DialogResult = true;
    }
}
