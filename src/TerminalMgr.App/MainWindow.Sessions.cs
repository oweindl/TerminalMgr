using System.Windows;
using System.Windows.Media;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// Note 16 (Bedienung Sessions und tmux): icon "Läufe" with badge, Alt numbers of open Makyx tabs in the runs panel, the context bar above
/// the terminal of a Makyx tab and the window switcher.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Alt number of the open tab that shows this run, for its line under the project.</summary>
    private string? AltLabelForRun(MakyxRun run) => _profiles.FirstOrDefault(p => p.MakyxRunId == run.Id && p.AltLabel.Length > 0)?.AltLabel;

    private void UpdateRunsBadge(int waiting)
    {
        RunsBadge.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunsBadgeText.Text = waiting > 99 ? "99+" : waiting.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RunsButton.ToolTip = "Läufe (" + _settings.Shortcuts.Normalized().Runs + ")" + (waiting > 0 ? " · " + waiting + " wartet" : "");
        System.Windows.Automation.AutomationProperties.SetName(RunsButton, waiting > 0 ? $"Läufe, {waiting} wartet" : "Läufe");
        NewSessionButton.ToolTip = "Neue Claude-Session (" + _settings.Shortcuts.Normalized().NewSession + ")";
    }

    private void ShowRunsClick(object sender, RoutedEventArgs e) => ShowRuns();

    /// <summary>Icon "Läufe" (Ctrl+Shift+L): opens the server panel with the runs unfolded and puts the focus there; without Makyx the settings.</summary>
    private void ShowRuns()
    {
        RunsButton.IsChecked = false;
        if (!Makyx.Enabled) { StatusText.Text = "Makyx ist nicht eingerichtet."; OpenSettings(this, new RoutedEventArgs()); return; }
        SetServerPanel(true);
        if (Makyx.Collapsed) { _settings = _settings with { Makyx = Makyx with { RunsOpen = true } }; ApplySidebarLayout(); RenderMakyx(); }
        SaveSettings();
        Dispatcher.BeginInvoke(() =>
        {
            MakyxScroll.ScrollToTop();
            if (MakyxRuns.Children.Count > 0 && MakyxRuns.Children[0] is UIElement first && first.Focusable) first.Focus(); else MakyxCollapseButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void NewSessionClick(object sender, RoutedEventArgs e) => OpenNewMakyxSession();

    /// <summary>The run behind the selected tab, if it is a Makyx tab and the run is still known.</summary>
    private MakyxRun? SelectedRun() =>
        Selected is { MakyxRunId: { } id } && _makyx is { } monitor ? monitor.Runs().FirstOrDefault(r => r.Id == id) : null;

    /// <summary>Shows the context bar for a Makyx tab: "Makyx › Projekt › Fenster", machine and tmux target, status.</summary>
    private void UpdateContextBar()
    {
        if (ContextBar is null) return;
        var item = Selected;
        if (item?.TmuxSocket is null) { ContextBar.Visibility = Visibility.Collapsed; return; }
        var run = SelectedRun();
        ContextBar.Visibility = Visibility.Visible;
        ContextSwitcherText.Text = "Fenster wechseln (" + _settings.Shortcuts.Normalized().WindowSwitcher + ")";
        if (run is null)
        {
            ContextPath.Text = "Makyx › " + item.Name;
            ContextTarget.Text = (item.Profile.Destination.Length > 0 ? item.Profile.Destination + " · " : "") + "tmux " + item.TmuxSocket;
            ContextStatus.Visibility = Visibility.Collapsed;
            ContextOrderButton.IsEnabled = false;
            return;
        }
        ContextPath.Text = "Makyx › " + MakyxNames.ProjectLabel(run) + " › " + MakyxNames.ShortName(run);
        ContextPath.ToolTip = run.Id + " · " + run.Title;
        string machine = run.Terminal.Machine ?? run.Machine ?? item.Profile.Destination;
        ContextTarget.Text = machine + " · tmux " + run.Terminal.TmuxSession + ":" + run.Terminal.TmuxWindow;
        ContextStatus.Visibility = Visibility.Visible;
        ContextStatusDot.Fill = MakyxRunsPanel.DotBrush(run.Status);
        ContextStatusText.Text = MakyxJson.StatusText(run.Status);
        ContextStatusText.Foreground = run.Status == MakyxStatus.Waiting ? MakyxRunsPanel.DotBrush(MakyxStatus.Waiting) : (Brush)FindResource("TextBrush");
        ContextOrderButton.IsEnabled = _makyx?.Waiting().Any(w => w.Reference == run.Id || w.Reference == run.Uuid) == true || run.Status == MakyxStatus.Waiting;
    }

    private void ContextSwitcherClick(object sender, RoutedEventArgs e) { if (Selected is { } item) OpenWindowSwitcher(item); }

    /// <summary>"Auftrag senden": answers what waits for this run (gate or question) through Makyx; without a waiting item it opens the run in Makyx.</summary>
    private void ContextOrderClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRun() is not { } run) return;
        var waiting = _makyx?.Waiting().FirstOrDefault(w => (w.Reference == run.Id || w.Reference == run.Uuid) && MakyxControl.IsAnswerable(w));
        if (waiting is not null) { _ = AnswerMakyxAsync(waiting, MakyxDecision.Return); return; }
        StatusText.Text = "Für diese Session wartet gerade nichts in Makyx; die Eingabe geht direkt ins Terminal.";
        FocusTerminal(Selected?.Profile.Id);
    }

    /// <summary>
    /// The window switcher (note 16, mockup C): all windows of this server, normal tmux and the Makyx socket, grouped; Enter shows the window in
    /// this tab, Ctrl+Enter in a new tab, N opens a new window, R renames.
    /// </summary>
    private async void OpenWindowSwitcher(ProfileItem item)
    {
        var basis = item.Temporary ? _profiles.FirstOrDefault(p => !p.Temporary && p.Profile.Destination == item.Profile.Destination) ?? item : item;
        StatusText.Text = "Fenster werden geladen …";
        List<TmuxSwitcherEntry> entries;
        try
        {
            var runs = _makyx?.Runs() ?? [];
            string? here = item.TmuxView is not null ? null : _tmuxAttached.TryGetValue(item.Profile.Id, out var attached) ? attached.Id : null;
            entries = [];
            if (item.TmuxSocket is null || basis != item)
            {
                var plain = await _tmuxQuery.ListWindowsAsync(basis.Profile, TimeSpan.FromSeconds(10), _shutdown.Token);
                entries.AddRange(TmuxSwitcher.PlainEntries(TmuxWindowParser.Group(plain), w => item.TmuxSocket is null && here is not null && w.SessionId == here && w.Active));
            }
            IReadOnlyList<string> sockets = item.TmuxSocket is { } own ? new[] { own } : _makyx is null ? Array.Empty<string>() : MakyxTmux.SocketsFor(basis.Profile, _profiles.Where(p => !p.Temporary).Select(p => p.Profile).ToList(), _makyx.Machines(), Makyx.MachineProfiles);
            foreach (string socket in sockets)
            {
                try
                {
                    var windows = MakyxTmux.Sessions(await _tmuxQuery.ListWindowsAsync(basis.Profile, TimeSpan.FromSeconds(10), _shutdown.Token, socket));
                    var current = item.TmuxSocket == socket ? SelectedRun() : null;
                    entries.AddRange(TmuxSwitcher.MakyxEntries(socket, windows, runs, w => current?.Terminal.TmuxWindow is { } tw && (tw == w.Name || tw == w.WindowId)));
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException) { }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = item.Name + ": tmux-Fenster nicht verfügbar (" + ex.Message.Split('\n')[0] + ")";
            return;
        }
        StatusText.Text = "";
        if (entries.Count == 0) { StatusText.Text = "Keine tmux-Fenster auf " + basis.Name + "."; return; }
        var dialog = new TmuxSwitcherWindow(basis.Name, entries, _settings.Shortcuts.Normalized().WindowSwitcher) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Chosen is not { } chosen) { FocusTerminal(item.Profile.Id); return; }
        try
        {
        switch (dialog.Action)
        {
            case TmuxSwitcherAction.Show or TmuxSwitcherAction.NewTab:
                bool newTab = dialog.Action == TmuxSwitcherAction.NewTab;
                if (chosen.Socket is { } socket)
                {
                    if (!newTab && item.TmuxSocket == socket && item.TmuxView is not null) await SwitchToTmuxWindowAsync(item, chosen.Window);
                    else await OpenMakyxWindowAsync(basis, socket, chosen.Window, newTab);
                }
                else if (newTab) await OpenPlainWindowInNewTabAsync(basis, chosen.Window);
                else await SwitchToTmuxWindowAsync(basis, chosen.Window);
                break;
            case TmuxSwitcherAction.NewWindow:
                await RunInTmuxAsync(chosen.Socket == item.TmuxSocket ? item : basis, chosen, "new-window", "Neues tmux-Fenster angelegt.");
                break;
            case TmuxSwitcherAction.Rename when dialog.NewName is { } name:
                var run = chosen.Socket is { } s ? TmuxSwitcher.RunFor(chosen.Window, chosen.Window.SessionName, s, _makyx?.Runs() ?? []) : null;
                if (run is not null) await RenameMakyxSessionAsync(run, name);
                else await RunInTmuxAsync(chosen.Socket == item.TmuxSocket ? item : basis, chosen,
                    "rename-window -t " + chosen.Window.WindowId + " " + SshCommand.ShellQuote(name), "Fenster umbenannt.");
                break;
        }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { ShowError("Fenster wechseln fehlgeschlagen.", ex); }
    }

    /// <summary>A tmux command in the client of a connected entry (N, R in the window switcher).</summary>
    private async Task RunInTmuxAsync(ProfileItem item, TmuxSwitcherEntry entry, string command, string done)
    {
        if (!_sessions.TryGetValue(item.Profile.Id, out var pty) || !await ClientInTmuxAsync(item, _shutdown.Token))
        {
            await SwitchToTmuxWindowAsync(item, entry.Window);
            if (!_sessions.TryGetValue(item.Profile.Id, out pty)) return;
        }
        await TmuxClient.RunCommandAsync(pty, command, _shutdown.Token);
        StatusText.Text = done;
        FocusTerminal(item.Profile.Id);
    }

    /// <summary>Ctrl+Enter on a window of the normal tmux server: a further entry "server · window" attached to it, not saved.</summary>
    private async Task OpenPlainWindowInNewTabAsync(ProfileItem basis, TmuxWindowRecord window)
    {
        try
        {
            string command = SshCommand.TmuxAttachArguments(basis.Profile, window.SessionId, window.WindowId, null)[^1];
            string name = basis.Name + " · " + window.Name;
            for (int n = 2; _profiles.Any(p => p.Profile.Name == name); n++) name = basis.Name + " · " + window.Name + " (" + n + ")";
            var profile = basis.Profile with { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = StartMode.Command, Command = command, AutoConnect = false, AutoReconnect = false };
            var item = new ProfileItem(profile) { Temporary = true };
            _profiles.Add(item);
            ProfilesList.SelectedItem = item;
            await ConnectAsync(item);
        }
        catch (ArgumentException) { StatusText.Text = "Ungültiges tmux-Fenster."; }
    }
}
