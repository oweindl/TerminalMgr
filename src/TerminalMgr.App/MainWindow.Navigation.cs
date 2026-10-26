using System.Windows;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// Keyboard navigation: tmux window by index, next, previous, back (also across servers) and favorites. Every switch goes through the
/// tmux command prompt of the own client (see <see cref="TmuxClient.RunCommandAsync"/>), never as keystrokes for the program.
/// </summary>
public partial class MainWindow
{
    private readonly NavigationHistory _history = new();
    private readonly SemaphoreSlim _navigationGate = new(1, 1);

    /// <summary>All shortcuts the terminal page has to take before the session sees them: dictation, copy, paste and the navigation ones.</summary>
    private Dictionary<string, string> ShortcutTable(ShortcutOptions options)
    {
        var table = new Dictionary<string, string> { ["dictation"] = DictationSettings.Shortcut, ["copy"] = options.Copy, ["paste"] = options.Paste };
        foreach (var navigation in ShortcutRules.NavigationShortcuts(_settings)) table[navigation.Action] = navigation.Shortcut.ToString();
        return table;
    }

    /// <summary>The action behind a pressed shortcut, from the page or from a WPF control, or null when it is none of the navigation actions.</summary>
    private string? NavigationActionFor(Shortcut pressed) =>
        ShortcutRules.NavigationShortcuts(_settings).FirstOrDefault(a => a.Shortcut == pressed)?.Action;

    /// <summary>Runs an action of the shortcut table (window0 to window9, windowNext, windowPrevious, windowBack, favorite:id).</summary>
    private void RunNavigationAction(string action) => _ = RunNavigationActionAsync(action);

    private async Task RunNavigationActionAsync(string action)
    {
        if (_closing) return;
        if (action == "palette") { OpenPaletteFromHost(); return; }
        await _navigationGate.WaitAsync(_shutdown.Token).ConfigureAwait(true);
        try
        {
            if (action.StartsWith("favorite:")) { await RunFavoriteAsync(action["favorite:".Length..]); return; }
            if (action == "windowBack") { await GoBackAsync(); return; }
            string? command = action switch
            {
                "windowNext" => TmuxNavigation.Next,
                "windowPrevious" => TmuxNavigation.Previous,
                _ when action.StartsWith("windowId:", StringComparison.Ordinal) && TmuxWindowParser.IsWindowId(action["windowId:".Length..]) => TmuxNavigation.SelectWindowId(action["windowId:".Length..]),
                _ when action.Length == 7 && action.StartsWith("window") && char.IsAsciiDigit(action[6]) => TmuxNavigation.SelectIndex(action[6] - '0'),
                _ => null
            };
            if (command is not null) await RunWindowCommandAsync(command);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { StatusText.Text = "Navigation: " + ex.Message; }
        finally { _navigationGate.Release(); }
    }

    /// <summary>Window of the current session of the selected server: index, next or previous.</summary>
    private async Task RunWindowCommandAsync(string command)
    {
        if (Selected is not { } item || !_sessions.TryGetValue(item.Profile.Id, out var pty))
        {
            StatusText.Text = "Zum Wechseln des tmux-Fensters zuerst einen verbundenen Server auswählen.";
            return;
        }
        // The starting point is needed for "back"; it is looked up once per server, before the first switch.
        if (_history.Current?.ProfileId != item.Profile.Id) await RecordPositionAsync(item, TimeSpan.FromSeconds(2));
        await TmuxClient.RunCommandAsync(pty, command, _shutdown.Token);
        FocusTerminal(item.Profile.Id);
        _ = RecordPositionAfterSwitchAsync(item);
    }

    private async Task RecordPositionAfterSwitchAsync(ProfileItem item)
    {
        await Task.Delay(350);
        await RecordPositionAsync(item, TimeSpan.FromSeconds(5));
        _ = PollStripAsync(force: true);
    }

    /// <summary>Asks the server which window the attached session shows now and puts it at the front of the history.</summary>
    private async Task RecordPositionAsync(ProfileItem item, TimeSpan limit)
    {
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cancel.CancelAfter(limit);
            var windows = await _tmuxQuery.ListWindowsAsync(item.Profile, limit, cancel.Token);
            var (attachedId, attachedName) = await AttachedTmuxAsync(item, cancel.Token);
            var sessions = TmuxWindowParser.Group(windows);
            var session = sessions.FirstOrDefault(s => attachedId is not null && s.Id == attachedId)
                ?? sessions.FirstOrDefault(s => attachedName is not null && s.Name == attachedName)
                ?? (sessions.Count == 1 ? sessions[0] : null);
            if (session?.Windows.FirstOrDefault(w => w.Active) is not { } window) return;
            _history.Visit(new(item.Profile.Id, window.SessionId, window.SessionName, window.WindowId, window.Name));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Back to the window used before, on any server. Windows that no longer exist are dropped from the history.</summary>
    private async Task GoBackAsync()
    {
        if (Selected is { } here && _history.Current is null) await RecordPositionAsync(here, TimeSpan.FromSeconds(2));
        while (_history.Previous is { } target)
        {
            var item = _profiles.FirstOrDefault(p => p.Profile.Id == target.ProfileId);
            if (item is null) { _history.Forget(p => p.ProfileId == target.ProfileId); continue; }
            IReadOnlyList<TmuxWindowRecord> windows;
            try { windows = await _tmuxQuery.ListWindowsAsync(item.Profile, TimeSpan.FromSeconds(6), _shutdown.Token); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                StatusText.Text = $"Zurück: {item.Name} ist nicht erreichbar.";
                return;
            }
            var window = windows.FirstOrDefault(w => w.SessionId == target.SessionId && w.WindowId == target.WindowId);
            if (window is null)
            {
                _history.Forget(p => p.SameWindow(target));
                continue;
            }
            await SwitchToTmuxWindowAsync(item, window);
            return;
        }
        StatusText.Text = "Es gibt noch kein früheres Fenster, zu dem man zurückkehren könnte.";
    }

    /// <summary>Favorite: look the window up by session and window name on the server (connected or not), connect if necessary, switch.</summary>
    private async Task RunFavoriteAsync(string favoriteId)
    {
        var favorite = _settings.Favorites.FirstOrDefault(f => f.Id == favoriteId);
        if (favorite is null) return;
        var item = _profiles.FirstOrDefault(p => p.Profile.Id == favorite.ProfileId);
        if (item is null) { StatusText.Text = $"Favorit „{favorite.Label}“: Der Server ist nicht mehr in der Liste."; return; }
        StatusText.Text = $"Favorit „{favorite.Label}“ …";
        IReadOnlyList<TmuxWindowRecord> windows;
        try { windows = await _tmuxQuery.ListWindowsAsync(item.Profile, TimeSpan.FromSeconds(10), _shutdown.Token); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            StatusText.Text = $"Favorit „{favorite.Label}“: {item.Name} ist nicht erreichbar.";
            return;
        }
        var resolution = FavoriteResolver.Resolve(favorite, windows);
        if (resolution.Window is not { } window) { StatusText.Text = resolution.Message; return; }
        await SwitchToTmuxWindowAsync(item, window);
        StatusText.Text = $"Favorit „{favorite.Label}“: {item.Name} › {window.SessionName} › {window.Name}";
    }

    /// <summary>The windows of the history, newest first, for the favorites dialog.</summary>
    private IReadOnlyList<NavigationPosition> RecentWindows => _history.Items;
}
