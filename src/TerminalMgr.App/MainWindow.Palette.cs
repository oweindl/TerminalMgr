using System.Text.Json;
using System.Windows;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// Command palette (Ctrl+Shift+K). The overlay lives in the terminal page (HTML, so that it is not hidden behind the WebView2);
/// this part builds the list, keeps a cache of the tmux windows of the connected servers and runs what the user picked.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, IReadOnlyList<TmuxWindowRecord>> _paletteWindows = [];
    private int _paletteRefresh;

    private IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var servers = _profiles.Select(p =>
        {
            bool connected = _sessions.ContainsKey(p.Profile.Id);
            var attached = _tmuxAttached.TryGetValue(p.Profile.Id, out var known) ? known.Item1 : null;
            _paletteWindows.TryGetValue(p.Profile.Id, out var windows);
            return new PaletteServer(p.Profile.Id, p.Name, connected, attached, windows ?? []);
        }).ToList();
        string Keys(string action) => ShortcutRules.NavigationShortcuts(_settings).FirstOrDefault(a => a.Action == action)?.Shortcut.ToString() ?? "";
        var shortcuts = _settings.Shortcuts.Normalized();
        var actions = new List<PaletteAction>
        {
            new("dictation", "Diktat starten", DictationSettings.Shortcut),
            new("connect", "Ausgewählten Server verbinden", ""),
            new("disconnect", "Ausgewählten Server trennen", ""),
            new("newProfile", "Neues Profil anlegen", ""),
            new("settings", "Einstellungen öffnen", ""),
            new("export", "Konfiguration exportieren", ""),
            new("import", "Konfiguration importieren", ""),
            new("update", "Nach Updates suchen", ""),
            new("back", "Zurück zum zuletzt benutzten Fenster", Keys("windowBack")),
            new("next", "Nächstes tmux-Fenster", shortcuts.NextWindow),
            new("previous", "Voriges tmux-Fenster", shortcuts.PreviousWindow),
        };
        return PaletteBuilder.Build(_settings, servers, _history.Items, _clipList.Items.Select(c => c.Text).ToList(), actions);
    }

    private object PaletteMessageItems() => BuildPaletteItems().Select(i => new
    {
        id = i.Id, group = i.Group, title = i.Title, subtitle = i.Subtitle, shortcut = i.Shortcut, showWhenEmpty = i.ShowWhenEmpty, searchable = i.Searchable
    }).ToList();

    private void PostPaletteItems() => Post(new { type = "paletteItems", items = PaletteMessageItems() });

    /// <summary>The palette was opened from a WPF control: the page shows it with the cached list, the refresh follows.</summary>
    private void OpenPaletteFromHost()
    {
        if (!_ready) return;
        Post(new { type = "palette", cmd = "open", items = PaletteMessageItems() });
    }

    /// <summary>The page opened the palette with the old list: send the current one at once, then fetch the tmux windows of the connected servers.</summary>
    private async Task RefreshPaletteAsync()
    {
        int generation = ++_paletteRefresh;
        PostPaletteItems();
        var connected = _profiles.Where(p => _sessions.ContainsKey(p.Profile.Id)).ToList();
        await Task.WhenAll(connected.Select(async item =>
        {
            try
            {
                var windows = await _tmuxQuery.ListWindowsAsync(item.Profile, TimeSpan.FromSeconds(4), _shutdown.Token);
                _paletteWindows[item.Profile.Id] = windows;
                if (generation == _paletteRefresh) PostPaletteItems();
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }));
    }

    private void RunPaletteItem(string id)
    {
        if (id.StartsWith("favorite:")) { RunNavigationAction(id); return; }
        if (PaletteBuilder.TryParseWindowId(id, out var profileId, out var sessionId, out var windowId))
        {
            var item = _profiles.FirstOrDefault(p => p.Profile.Id == profileId);
            if (item is null) { StatusText.Text = "Der Server dieses Fensters ist nicht mehr in der Liste."; return; }
            var record = (_paletteWindows.GetValueOrDefault(profileId) ?? []).FirstOrDefault(w => w.SessionId == sessionId && w.WindowId == windowId)
                ?? _history.Items.Where(p => p.ProfileId == profileId && p.SessionId == sessionId && p.WindowId == windowId)
                    .Select(p => new TmuxWindowRecord(p.SessionId, p.SessionName, p.WindowId, 0, p.WindowName, "", false)).FirstOrDefault();
            if (record is null) return;
            _ = RunExclusiveAsync(() => SwitchToTmuxWindowAsync(item, record));
            return;
        }
        if (id.StartsWith("clip:") && int.TryParse(id["clip:".Length..], out int index) && index >= 0 && index < _clipList.Items.Count)
        {
            if (Selected is not { } target) { StatusText.Text = "Zum Einfügen zuerst eine Session auswählen."; return; }
            _clipList.Activate(_clipList.Items[index]);
            RenderClips();
            PasteActiveClip(target.Profile.Id);
            return;
        }
        if (!id.StartsWith("action:")) return;
        switch (id["action:".Length..])
        {
            case "dictation": ToggleDictation(); break;
            case "connect": ConnectSelected(this, new RoutedEventArgs()); break;
            case "disconnect": DisconnectSelected(this, new RoutedEventArgs()); break;
            case "newProfile": AddProfile(this, new RoutedEventArgs()); break;
            case "settings": OpenSettings(this, new RoutedEventArgs()); break;
            case "export": _ = ExportConfigAsync(); break;
            case "import": _ = ImportConfigAsync(); break;
            case "update": _ = CheckForUpdatesNowAsync(); break;
            case "back": RunNavigationAction("windowBack"); break;
            case "next": RunNavigationAction("windowNext"); break;
            case "previous": RunNavigationAction("windowPrevious"); break;
        }
    }

    private async Task RunExclusiveAsync(Func<Task> work)
    {
        await _navigationGate.WaitAsync(_shutdown.Token).ConfigureAwait(true);
        try { await work(); }
        catch (OperationCanceledException) when (_closing) { }
        finally { _navigationGate.Release(); }
    }

    /// <summary>Ctrl+D in the palette: save the highlighted window as a favorite (name and shortcut in a small dialog).</summary>
    private void SavePaletteFavorite(string id)
    {
        if (id.StartsWith("favorite:")) { StatusText.Text = "Das ist schon ein Favorit. Änderungen unter Einstellungen › Favoriten verwalten."; return; }
        if (!PaletteBuilder.TryParseWindowId(id, out var profileId, out var sessionId, out var windowId)) return;
        var record = (_paletteWindows.GetValueOrDefault(profileId) ?? []).FirstOrDefault(w => w.SessionId == sessionId && w.WindowId == windowId);
        var recent = _history.Items.FirstOrDefault(p => p.ProfileId == profileId && p.SessionId == sessionId && p.WindowId == windowId);
        string? session = record?.SessionName ?? recent?.SessionName, window = record?.Name ?? recent?.WindowName;
        if (session is null || window is null) return;
        string server = _profiles.FirstOrDefault(p => p.Profile.Id == profileId)?.Name ?? "";
        var dialog = new FavoriteSaveDialog(new Favorite { ProfileId = profileId, SessionName = session, WindowName = window, Name = window }, server) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } favorite) return;
        var candidate = _settings with { Favorites = [.. _settings.Favorites, favorite] };
        if (ShortcutRules.FindConflict(candidate) is { } conflict)
        {
            MessageBox.Show(this, conflict + "\n\nDer Favorit wurde nicht gespeichert.", "Favorit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings = candidate;
        SaveSettings();
        PostShortcuts();
        PostPaletteItems();
        StatusText.Text = $"Favorit „{favorite.Label}“ gespeichert" + (favorite.Shortcut.Length > 0 ? $" ({favorite.Shortcut})." : ".");
    }
}
