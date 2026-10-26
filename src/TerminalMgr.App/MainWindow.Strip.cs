using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// The strip of tmux windows below the title bar: the windows of the session the selected server shows, the ones Ctrl+0 to Ctrl+9
/// reach, each with its state (running, done, asking, idle), the last lines and an activity history. It is read with a separate
/// SSH call every few seconds and never touches the running programs. Off, the old server tabs are shown instead.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan StripInterval = TimeSpan.FromSeconds(5);
    private readonly ObservableCollection<StripCard> _stripCards = [];
    private readonly Dictionary<string, WindowActivityTracker> _trackers = [];
    private readonly Dictionary<string, string> _stripTargets = [];
    private DispatcherTimer? _stripTimer;
    private bool _stripOpen = true, _stripBusy;
    private DateTimeOffset _stripLastPoll = DateTimeOffset.MinValue;
    private int _stripCurrent = -1;

    private void InitializeStrip()
    {
        WindowStrip.ItemsSource = _stripCards;
        // Without a visible scroll bar the mouse wheel moves the strip sideways when there are more windows than fit.
        WindowStrip.PreviewMouseWheel += (_, e) =>
        {
            if (FindScrollViewer(WindowStrip) is not { } viewer || viewer.ScrollableWidth <= 0) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        _stripTimer = new DispatcherTimer(StripInterval, DispatcherPriority.Background, (_, _) => _ = PollStripAsync(), Dispatcher);
        _stripTimer.Start();
    }

    private void ToggleStrip(object sender, RoutedEventArgs e) { SetStrip(!_stripOpen); SaveSettings(); }

    private void SetStrip(bool open)
    {
        _stripOpen = open;
        StripButton.IsChecked = open;
        if (!open) _stripCards.Clear();
        UpdateStripVisibility();
        if (open) _ = PollStripAsync(force: true);
    }

    /// <summary>The strip replaces the server tabs while it has something to show; otherwise (off, no tmux, not connected) the tabs stay.</summary>
    private void UpdateStripVisibility()
    {
        bool show = _stripOpen && _stripCards.Count > 0;
        WindowStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SessionTabs.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearStrip()
    {
        if (_stripCards.Count == 0) return;
        _stripCards.Clear();
        UpdateStripVisibility();
    }

    private async Task PollStripAsync(bool force = false)
    {
        if (_closing || _stripBusy) return;
        // In the background a slow rhythm is enough; the strip catches up when the window comes back.
        if (!force && !IsActive && DateTimeOffset.Now - _stripLastPoll < TimeSpan.FromSeconds(30)) return;
        if (!_stripOpen || Selected is not { } item || item.Connection != ConnectionState.Connected || !_sessions.ContainsKey(item.Profile.Id))
        {
            ClearStrip();
            return;
        }
        _stripBusy = true;
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cancel.CancelAfter(TimeSpan.FromSeconds(9));
            string profileId = item.Profile.Id;
            if (!_stripTargets.TryGetValue(profileId, out string? target))
            {
                var (sessionId, sessionName) = await AttachedTmuxAsync(item, cancel.Token);
                target = sessionId ?? sessionName;
                if (target is null) { ClearStrip(); return; }
                _stripTargets[profileId] = target;
            }
            var poll = await _tmuxQuery.StripAsync(item.Profile, target, TimeSpan.FromSeconds(8), cancel.Token);
            _stripLastPoll = DateTimeOffset.Now;
            if (!ReferenceEquals(Selected, item) || !_stripOpen) return;     // the user moved on while the answer was on its way
            if (!_trackers.TryGetValue(profileId, out var tracker)) _trackers[profileId] = tracker = new WindowActivityTracker();
            ShowCards(tracker.Update(poll, DateTimeOffset.Now, StripInterval.TotalSeconds));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // No tmux in this session, no key login for the query, a slow server: the strip simply is not there (the tabs stay).
            if (ex is InvalidOperationException) _stripTargets.Remove(Selected?.Profile.Id ?? "");
            ClearStrip();
        }
        finally { _stripBusy = false; }
    }

    private void ShowCards(IReadOnlyList<WindowCard> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var card = new StripCard(cards[i]);
            if (i < _stripCards.Count) _stripCards[i] = card; else _stripCards.Add(card);
        }
        while (_stripCards.Count > cards.Count) _stripCards.RemoveAt(_stripCards.Count - 1);
        UpdateStripVisibility();
        // When the current window changes (also through Ctrl+number) it is brought into view.
        int current = cards.ToList().FindIndex(c => c.Current);
        if (current >= 0 && current != _stripCurrent && WindowStrip.IsVisible) WindowStrip.ScrollIntoView(_stripCards[current]);
        _stripCurrent = current;
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>A click on a card switches the session to that window, the same way Ctrl+number does.</summary>
    private void StripCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StripCard card }) { RunNavigationAction("windowId:" + card.WindowId); e.Handled = true; }
    }

    /// <summary>The server changed: the old cards belong to another session.</summary>
    private void StripSelectionChanged()
    {
        _stripCards.Clear();
        UpdateStripVisibility();
        _ = PollStripAsync(force: true);
    }
}
