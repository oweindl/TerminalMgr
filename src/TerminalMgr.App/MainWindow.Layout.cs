using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// The "Commander" layout: icon bar, server panel that opens from the bar, session tabs, right column. This part holds the state of those
/// areas (panels open or closed, focus mode), the tabs, the theme hand-over to the terminal page and the toast that replaced the status bar.
/// </summary>
public partial class MainWindow
{
    private const double ServerPanelWidth = 260, ClipboardPanelWidth = 312;
    private readonly ObservableCollection<TabModel> _tabs = [];
    private bool _updatingTabs, _focusMode, _focusRestoreServer, _focusRestoreClipboard;

    private void InitializeLayout()
    {
        SessionTabs.ItemsSource = _tabs;
        _profiles.CollectionChanged += ProfilesChanged;
        // Messages that used to go to the status bar appear as a toast in the terminal page.
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(StatusText, (_, _) => ShowToast(StatusText.Text));
        ApplyPaletteHint();
    }

    private bool ServerPanelOpen => ServerColumn.Width.Value > 0;
    private bool ClipboardOpen => ClipboardColumn.Width.Value > 0;

    private void SetServerPanel(bool open)
    {
        if (open == ServerPanelOpen) { ServerButton.IsChecked = open; return; }
        if (!open) _serverWidth = ServerColumn.ActualWidth > 40 ? ServerColumn.ActualWidth : ServerPanelWidth;
        ServerColumn.Width = new GridLength(open ? Math.Max(_serverWidth, 180) : 0);
        ServerSplitterColumn.Width = new GridLength(open ? 4 : 0);
        ServerPanel.Visibility = ServerSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ServerButton.IsChecked = open;
        if (open) ProfilesList.Focus();
    }

    private void SetClipboard(bool open)
    {
        if (open == ClipboardOpen) { ClipboardButton.IsChecked = open; return; }
        if (!open) _clipWidth = ClipboardColumn.ActualWidth > 40 ? ClipboardColumn.ActualWidth : ClipboardPanelWidth;
        ClipboardColumn.Width = new GridLength(open ? Math.Max(_clipWidth, 220) : 0);
        ClipboardSplitterColumn.Width = new GridLength(open ? 4 : 0);
        ClipboardPanel.Visibility = ClipboardSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ClipboardButton.IsChecked = open;
    }

    private void ToggleServerPanel(object sender, RoutedEventArgs e) => SetServerPanel(!ServerPanelOpen);
    private void ToggleClipboard(object sender, RoutedEventArgs e) => SetClipboard(!ClipboardOpen);

    /// <summary>Focus mode: side areas out of the way. Leaving it brings back exactly what was open before.</summary>
    private void ToggleFocus(object sender, RoutedEventArgs e)
    {
        if (!_focusMode)
        {
            _focusRestoreServer = ServerPanelOpen; _focusRestoreClipboard = ClipboardOpen;
            SetServerPanel(false); SetClipboard(false);
            _focusMode = true;
        }
        else
        {
            _focusMode = false;
            SetServerPanel(_focusRestoreServer); SetClipboard(_focusRestoreClipboard);
        }
        StatusText.Text = _focusMode ? "Fokusmodus an (Seitenbereiche ausgeblendet)" : "Fokusmodus aus";
    }

    /// <summary>The shortcut of the palette in the hint of the tab strip.</summary>
    private void ApplyPaletteHint() => TabHint.Text = _settings.Shortcuts.Normalized().Palette.Replace("Shift", "⇧").Replace("+", " ") + " alles finden";

    // ---- tabs ----

    private void ProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (ProfileItem item in e.OldItems) item.PropertyChanged -= ProfileItemChanged;
        if (e.NewItems is not null) foreach (ProfileItem item in e.NewItems) item.PropertyChanged += ProfileItemChanged;
        RefreshTabs();
    }

    private void ProfileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileItem.Connection) or nameof(ProfileItem.Name)) RefreshTabs();
    }

    /// <summary>One tab per connected server, plus the selected one (so the terminal always has its tab). The number is the Alt number.</summary>
    private void RefreshTabs()
    {
        if (_updatingTabs || SessionTabs is null) return;
        _updatingTabs = true;
        try
        {
            var wanted = _profiles.Select((item, index) => (item, index))
                .Where(t => t.item.Connection != ConnectionState.Disconnected || ReferenceEquals(t.item, Selected)).ToList();
            for (int i = _tabs.Count - 1; i >= 0; i--)
                if (wanted.All(w => !ReferenceEquals(w.item, _tabs[i].Item))) _tabs.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                var (item, index) = wanted[i];
                int at = _tabs.ToList().FindIndex(t => ReferenceEquals(t.Item, item));
                string number = index < 9 ? (index + 1).ToString() : "";
                if (at < 0) _tabs.Insert(Math.Min(i, _tabs.Count), new TabModel(item, number));
                else if (at != i) _tabs.Move(at, Math.Min(i, _tabs.Count - 1));
            }
            SessionTabs.SelectedItem = _tabs.FirstOrDefault(t => ReferenceEquals(t.Item, Selected));
        }
        finally { _updatingTabs = false; }
    }

    private void TabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTabs || SessionTabs.SelectedItem is not TabModel tab) return;
        if (!ReferenceEquals(Selected, tab.Item)) ProfilesList.SelectedItem = tab.Item;
    }

    private void TabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(SessionTabs, source) is not ListBoxItem { DataContext: TabModel tab } container) return;
        ProfilesList.SelectedItem = tab.Item;
        ShowServerMenu(tab.Item, container);
        e.Handled = true;
    }

    // ---- theme and toast ----

    /// <summary>The terminal page gets the colors and fonts of <see cref="Theme.Dark"/> as CSS variables (the same source as the WPF brushes).</summary>
    private void PostTheme() => Post(new { type = "theme", variables = Theme.Dark.CssVariables() });

    private void ShowToast(string text)
    {
        if (!_ready || _closed) return;
        Post(new { type = "toast", text });
    }
}
