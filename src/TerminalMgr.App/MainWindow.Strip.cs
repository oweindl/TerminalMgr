using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// The strip of tmux windows below the title bar: the windows of the session the selected server shows, the ones Ctrl+0 to Ctrl+9
/// reach, each with its state (running, done, asking, idle), the lines that appeared last and an activity history. One long-lived
/// SSH connection delivers the screens of all windows once a second (see <see cref="TmuxStripStream"/>); the state comes from whether
/// the screens change. Nothing is sent into the running programs. Off, the old server tabs are shown instead.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan StripRetry = TimeSpan.FromSeconds(10);
    private readonly ObservableCollection<StripCard> _stripCards = [];
    private readonly Dictionary<string, WindowActivityTracker> _trackers = [];
    private readonly Dictionary<string, string> _stripTargets = [];
    private DispatcherTimer? _stripTimer;
    private TmuxStripStream? _stream;
    private string? _streamProfileId, _streamTarget;
    private int _streamGeneration, _stripCurrent = -1;
    private bool _stripOpen = true, _stripStarting;
    private DateTimeOffset _streamRetryAt = DateTimeOffset.MinValue;

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
        // The connection itself delivers the frames; this timer only checks that it is running for the selected, connected server.
        _stripTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => EnsureStrip(), Dispatcher);
        _stripTimer.Start();
    }

    private void ToggleStrip(object sender, RoutedEventArgs e) { SetStrip(!_stripOpen); SaveSettings(); }

    private void SetStrip(bool open)
    {
        _stripOpen = open;
        StripButton.IsChecked = open;
        if (!open) { StopStream(); _stripCards.Clear(); }
        UpdateStripVisibility();
        EnsureStrip();
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

    /// <summary>Starts, keeps or stops the frame connection according to what is selected and connected.</summary>
    private void EnsureStrip()
    {
        if (_closing) return;
        if (!_stripOpen || Selected is not { } item || item.Connection != ConnectionState.Connected || !_sessions.ContainsKey(item.Profile.Id))
        {
            StopStream();
            ClearStrip();
            return;
        }
        if (_stream is not null && _streamProfileId == item.Profile.Id) return;
        if (_stripStarting || DateTimeOffset.Now < _streamRetryAt) return;
        _ = StartStreamAsync(item);
    }

    private async Task StartStreamAsync(ProfileItem item)
    {
        _stripStarting = true;
        try
        {
            string profileId = item.Profile.Id;
            if (!_stripTargets.TryGetValue(profileId, out string? target))
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                cancel.CancelAfter(TimeSpan.FromSeconds(8));
                var (sessionId, sessionName) = await AttachedTmuxAsync(item, cancel.Token);
                target = sessionId ?? sessionName;
                if (target is null) { _streamRetryAt = DateTimeOffset.Now + StripRetry; return; }
                _stripTargets[profileId] = target;
            }
            if (!ReferenceEquals(Selected, item) || !_stripOpen || _closing) return;
            StopStream();
            int generation = ++_streamGeneration;
            if (!_trackers.TryGetValue(profileId, out var tracker)) _trackers[profileId] = tracker = new WindowActivityTracker();
            tracker.Reset();      // the first frame is a baseline, not a change
            _stream = TmuxStripStream.Start(item.Profile, target,
                poll => Dispatcher.BeginInvoke(() => OnStripFrame(generation, item, tracker, poll)),
                reason => Dispatcher.BeginInvoke(() => OnStripEnded(generation, profileId, reason)));
            _streamProfileId = profileId;
            _streamTarget = target;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _streamRetryAt = DateTimeOffset.Now + StripRetry;
        }
        finally { _stripStarting = false; }
    }

    private void StopStream()
    {
        var stream = _stream;
        _stream = null; _streamProfileId = null; _streamTarget = null;
        _streamGeneration++;
        if (stream is not null) _ = stream.DisposeAsync().AsTask();
    }

    private void OnStripFrame(int generation, ProfileItem item, WindowActivityTracker tracker, WindowPoll poll)
    {
        if (generation != _streamGeneration || !ReferenceEquals(Selected, item) || !_stripOpen) return;
        ShowCards(tracker.Update(poll, DateTimeOffset.Now));
    }

    /// <summary>No tmux in this session, no key login for the query, the connection dropped: the strip is gone (the tabs stay) and tries again later.</summary>
    private void OnStripEnded(int generation, string profileId, string reason)
    {
        if (generation != _streamGeneration) return;
        StopStream();
        _stripTargets.Remove(profileId);
        _streamRetryAt = DateTimeOffset.Now + StripRetry;
        ClearStrip();
    }

    /// <summary>The server attaches to another tmux session (or reconnects): the cached session and the running connection are stale.</summary>
    private void StripSessionChanged(string profileId, string? sessionId)
    {
        if (sessionId is not null && _stripTargets.TryGetValue(profileId, out string? known) && known == sessionId) return;
        _stripTargets.Remove(profileId);
        if (_streamProfileId == profileId) { StopStream(); ClearStrip(); }
        _streamRetryAt = DateTimeOffset.MinValue;
        EnsureStrip();
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
        StopStream();
        _stripCards.Clear();
        UpdateStripVisibility();
        _streamRetryAt = DateTimeOffset.MinValue;
        EnsureStrip();
    }
}
