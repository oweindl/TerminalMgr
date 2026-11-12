using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Makyx dashboard: the LÄUFE strip under KI-ABOS, a toast for new "waits for you" items, the jump into the Makyx page or the tmux window of an instance, and the palette entries.
/// Steering (sessions, runs, answers, stop) lives in MainWindow.MakyxControl.cs.</summary>
public partial class MainWindow
{
    private MakyxMonitor? _makyx;
    private string _makyxState = "";
    private DispatcherTimer? _makyxClock;
    private MakyxOptions Makyx => _settings.Makyx;

    private void InitializeMakyx()
    {
        MakyxRuns.OpenRequested += run => OpenMakyxLink(run.Link);
        MakyxRuns.BrowserRequested += run => OpenMakyxInBrowser(run.Link);
        MakyxRuns.CopyRequested += run => { try { Clipboard.SetText(run.Link); StatusText.Text = "Link kopiert."; } catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "Der Link ließ sich nicht kopieren."; } };
        MakyxRuns.TerminalRequested += run => _ = OpenMakyxTerminalAsync(run);
        MakyxRuns.TerminalProblem = TerminalProblem;
        MakyxRuns.IsShown = IsInstanceShown;
        MakyxRuns.ShellProblem = machine => ShellProfileFor(machine) is null ? "kein SSH-Profil" : null;
        MakyxRuns.ShellRequested += machine => _ = OpenMakyxShellAsync(machine);
        InitializeMakyxControl();
        // Machines (connected or not) and projects change without an event: reload them with the clock.
        _makyxClock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) =>
        {
            if (_makyx is not { } monitor || Makyx.Collapsed) return;
            await monitor.RefreshProjectsAsync();
            if (ReferenceEquals(_makyx, monitor)) RenderMakyx();
        }, Dispatcher);
        _makyxClock.Start();
        RenderMakyx();      // shows "nicht eingerichtet" until the settings are loaded and the monitor starts
    }

    /// <summary>Starts, restarts or stops the monitor after the settings changed (or at start).</summary>
    private void ApplyMakyxSettings()
    {
        _makyx?.Dispose(); _makyx = null; _makyxState = "";
        string? token = null;
        if (Makyx.Enabled && Makyx.Problem() is null)
        {
            try { token = MakyxCredentialStore.Read(); } catch (System.ComponentModel.Win32Exception) { token = null; }
            if (token is null) _makyxState = "Token fehlt (Einstellungen)";
        }
        if (token is not null)
        {
            var monitor = new MakyxMonitor(Makyx, token);
            monitor.Changed += change => Dispatcher.BeginInvoke(() => OnMakyxChanged(monitor, change));
            monitor.ConnectionChanged += (state, _) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_makyx, monitor)) { _makyxState = state; RenderMakyx(); } });
            _makyx = monitor;
            monitor.Start();
        }
        ApplySidebarLayout();
        RenderMakyx();
    }

    private void OnMakyxChanged(MakyxMonitor monitor, MakyxChange change)
    {
        if (_closing || !ReferenceEquals(_makyx, monitor)) return;
        RenderMakyx();
        OpenPendingMakyxSession();
        if (!Makyx.Notify) return;
        foreach (var item in change.NewWaiting)
            ToastWindow.Show("Wartet auf dich", $"{MakyxJson.WaitingKindText(item)}: {item.Title}" + (item.Project is { Length: > 0 } p ? " · " + p : ""), () => OpenMakyxLink(item.Link));
    }

    private void RenderMakyx()
    {
        if (MakyxRuns is null) return;
        var runs = _makyx?.Runs() ?? [];
        var waitingItems = _makyx?.Waiting() ?? [];
        int waiting = waitingItems.Count;
        int active = runs.Count(r => r.IsActive);
        MakyxCount.Text = waiting > 0 ? waiting + " wartet" : active > 0 ? active + " aktiv" : "";
        MakyxCount.Foreground = waiting > 0 ? MakyxTreePanel.DotBrush(MakyxStatus.Waiting) : (System.Windows.Media.Brush)FindResource("Text2Brush");
        MakyxStateLabel.Text = StateText();
        MakyxStateLabel.Visibility = MakyxStateLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string empty = !Makyx.Enabled ? "Makyx nicht eingerichtet · Einstellungen › Makyx-Leitstand" : _makyx is null ? "Makyx nicht verbunden." : "Makyx meldet keine Rechner und Projekte.";
        var tree = _makyx is { } monitor ? MakyxTree.Build(monitor.Machines(), monitor.Projects(), runs) : [];
        MakyxRuns.Render(tree, waitingItems, empty);
    }

    /// <summary>The SSH profile of a Makyx machine (same rule as for the terminal of an instance).</summary>
    private ProfileItem? ShellProfileFor(MakyxMachine machine)
    {
        var profile = MakyxProfileMatcher.Match(_profiles.Where(p => !p.Temporary).Select(p => p.Profile).ToList(), MakyxControl.MachineValue(machine), machine.SshTarget, Makyx.MachineProfiles);
        return profile is null ? null : _profiles.FirstOrDefault(p => ReferenceEquals(p.Profile, profile) || p.Profile.Id == profile.Id);
    }

    /// <summary>"Shell öffnen" in the MAKYX tree: the server's own entry, connected (or just selected when it already runs).</summary>
    private async Task OpenMakyxShellAsync(MakyxMachine machine)
    {
        if (ShellProfileFor(machine) is not { } item) { StatusText.Text = "Es gibt kein SSH-Profil für " + MakyxControl.MachineValue(machine) + "."; return; }
        SelectProfile(item);
        if (!_sessions.ContainsKey(item.Profile.Id)) await ConnectAsync(item);
    }

    private string StateText()
    {
        if (!Makyx.Enabled) return "";
        if (Makyx.Problem() is { } problem) return "Makyx: " + problem;
        if (_makyx is null) return _makyxState;
        if (_makyxState is "connected" or "") return "";
        if (_makyxState == "connecting") return "verbinde …";
        string last = _makyx.LastContact is { } t ? " · letzter Stand " + t.ToString("HH:mm") : "";
        return _makyxState + last;
    }

    private void ToggleMakyxPanel(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { Makyx = Makyx with { RunsOpen = Makyx.Collapsed } };
        ApplySidebarLayout(); SaveSettings(); RenderMakyx();
    }

    private void MakyxMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "Neu verbinden", IsEnabled = Makyx.Enabled };
        refresh.Click += (_, _) => ApplyMakyxSettings(); menu.Items.Add(refresh);
        // The interface address is the origin of the links Makyx reports; without runs there is none yet.
        string? home = _makyx?.Runs().Select(r => MakyxBrowserRules.Origin(r.Link)).FirstOrDefault(o => o is not null);
        var open = new MenuItem { Header = "Makyx öffnen", IsEnabled = home is not null };
        open.Click += (_, _) => OpenMakyxLink(home + "/"); menu.Items.Add(open);
        AddMakyxControlMenus(menu);
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Makyx-Einstellungen …" };
        settings.Click += (_, _) => OpenSettings(this, new RoutedEventArgs()); menu.Items.Add(settings);
        menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }

    /// <summary>Opens a Makyx page in the Makyx window of TerminalMgr (note 15, stage 1); only http and https.</summary>
    private void OpenMakyxLink(string link)
    {
        string safe = MakyxJson.SafeLink(link);
        if (safe.Length == 0) { StatusText.Text = "Für diesen Eintrag gibt es keinen Link."; return; }
        MakyxBrowserWindow.Open(this, _dataDirectory, () => Makyx.CaFingerprint, safe, MakyxLoginCodeAsync);
    }

    /// <summary>One-time sign-in code for the Makyx window, fetched with the paired token; null (page asks for the password) when anything fails.</summary>
    private async Task<string?> MakyxLoginCodeAsync()
    {
        if (!Makyx.Enabled || Makyx.Problem() is not null) return null;
        string? token;
        try { token = MakyxCredentialStore.Read(); } catch (System.ComponentModel.Win32Exception) { return null; }
        if (token is null) return null;
        try
        {
            using var client = new MakyxClient(Makyx, token);
            return await client.LoginCodeAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is MakyxClient.MakyxException or ArgumentException)
        {
            StatusText.Text = "Makyx: Anmeldung per Token nicht möglich (" + ex.Message + ") – die Seite fragt nach dem Passwort.";
            return null;
        }
    }

    /// <summary>Only http and https are opened, in the default browser.</summary>
    private void OpenMakyxInBrowser(string link)
    {
        string safe = MakyxJson.SafeLink(link);
        if (safe.Length == 0) { StatusText.Text = "Für diesen Eintrag gibt es keinen Link."; return; }
        try { Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText.Text = "Der Browser ließ sich nicht öffnen."; }
    }

    private Profile? ProfileFor(MakyxRun run) => MakyxProfileMatcher.Match(_profiles.Where(p => !p.Temporary).Select(p => p.Profile).ToList(), run.Terminal.Machine ?? run.Machine, run.Terminal.SshTarget, Makyx.MachineProfiles);

    private string? TerminalProblem(MakyxRun run) => run.Terminal.Available
        ? (ProfileFor(run) is null ? "kein SSH-Profil für " + (run.Terminal.Machine ?? "diesen Rechner") : null)
        : "kein Terminal";

    /// <summary>"Im Terminal öffnen": a second SSH connection with the profile of the machine, attached to the tmux window of the instance. It appears as its own entry "Makyx · window" and is not saved.</summary>
    private async Task OpenMakyxTerminalAsync(MakyxRun run)
    {
        if (!run.Terminal.Available || run.Terminal is not { TmuxSocket: { } socket, TmuxSession: { } session, TmuxWindow: { } window }) { StatusText.Text = "Diese Instanz hat kein Terminal."; return; }
        var basis = ProfileFor(run);
        if (basis is null) { StatusText.Text = "Es gibt kein SSH-Profil für " + (run.Terminal.Machine ?? "diesen Rechner") + " (Profil mit gleichem Namen oder Ziel anlegen)."; return; }
        try
        {
            var args = SshCommand.MakyxTmuxAttachArguments(basis, socket, session, window); // validates the names
            string command = args[^1];
            // The same tmux window opens the same entry again; a second instance of the same project on the same machine gets its id appended.
            var existing = _profiles.FirstOrDefault(p => p.Temporary && p.Profile.Command == command && p.Profile.Destination == basis.Destination);
            if (existing is not null) { SelectProfile(existing); await ConnectAsync(existing); return; }
            string name = MakyxControl.TerminalName(run);
            if (_profiles.Any(p => p.Profile.Name == name)) name += " · " + run.Id;
            var profile = basis with { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = StartMode.Command, Command = command, AutoConnect = false, AutoReconnect = false };
            var item = new ProfileItem(profile) { Temporary = true };
            _profiles.Add(item);
            SelectProfile(item);
            await ConnectAsync(item);
        }
        catch (ArgumentException) { StatusText.Text = "Der tmux-Name von Makyx ist ungültig."; }
    }

    /// <summary>
    /// Windows of the Makyx instances that run on this server, per tmux socket of Makyx (from <c>/rechner</c>). Empty without Makyx,
    /// for entries that already are on a Makyx socket, and when the socket does not answer; the server menu then shows only the normal sessions.
    /// </summary>
    private async Task<IReadOnlyList<(string Socket, IReadOnlyList<TmuxSessionWindows> Sessions)>> MakyxWindowsForAsync(ProfileItem item, CancellationToken cancel)
    {
        if (_makyx is null || item.Temporary || item.TmuxSocket is not null) return [];
        var profiles = _profiles.Where(p => !p.Temporary).Select(p => p.Profile).ToList();
        var result = new List<(string, IReadOnlyList<TmuxSessionWindows>)>();
        foreach (string socket in MakyxTmux.SocketsFor(item.Profile, profiles, _makyx.Machines(), Makyx.MachineProfiles))
        {
            try
            {
                var sessions = MakyxTmux.Sessions(await _tmuxQuery.ListWindowsAsync(item.Profile, TimeSpan.FromSeconds(10), cancel, socket));
                if (sessions.Count > 0) result.Add((socket, sessions));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    /// <summary>
    /// Opens a Makyx window from the server menu: one entry "Makyx · server" per server and socket (not saved), attached to the session of
    /// that window; further windows switch inside it, and the window strip shows all windows of the Makyx session.
    /// </summary>
    private async Task OpenMakyxWindowAsync(ProfileItem basis, string socket, TmuxWindowRecord window)
    {
        try
        {
            string name = MakyxTmux.GroupPrefix + basis.Name;
            var item = _profiles.FirstOrDefault(p => p.Temporary && p.TmuxSocket == socket && p.Profile.Name == name && p.Profile.Destination == basis.Profile.Destination);
            if (item is null)
            {
                string command = SshCommand.TmuxAttachArguments(basis.Profile, window.SessionId, window.WindowId, socket)[^1]; // validates socket and IDs
                var profile = basis.Profile with { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = StartMode.Command, Command = command, AutoConnect = false, AutoReconnect = false };
                item = new ProfileItem(profile) { Temporary = true, TmuxSocket = socket };
                _profiles.Add(item);
            }
            await SwitchToTmuxWindowAsync(item, window);
        }
        catch (ArgumentException) { StatusText.Text = "Der tmux-Name von Makyx ist ungültig."; }
    }

    /// <summary>
    /// A temporary entry (Makyx instance or "Makyx · server") whose connection ended: it can not be opened again (the tmux window is
    /// usually gone), so it leaves the list together with its terminal and tab instead of staying there as a dead entry.
    /// </summary>
    private void DropTemporary(ProfileItem item, string reason)
    {
        if (!item.Temporary || _closing || !_profiles.Contains(item)) return;
        string id = item.Profile.Id;
        CancelReconnect(id, reset: true);
        _reconnect.Remove(id);
        if (_terminals.Remove(id)) Post(new { type = "remove", id });
        bool wasSelected = ReferenceEquals(Selected, item);
        _profiles.Remove(item);
        if (wasSelected)
        {
            var numbered = NumberedProfiles();
            SelectProfile(numbered.FirstOrDefault(p => p.Connection == ConnectionState.Connected) ?? numbered.FirstOrDefault());
        }
        StatusText.Text = item.Name + ": " + reason + " – Eintrag entfernt.";
    }

    /// <summary>The ssh command that attaches to the tmux window of an instance (the identity of its temporary terminal entry), or null.</summary>
    private string? MakyxAttachCommand(MakyxRun run)
    {
        if (run.Terminal is not { Available: true, TmuxSocket: { } socket, TmuxSession: { } session, TmuxWindow: { } window } || ProfileFor(run) is not { } basis) return null;
        try { return SshCommand.MakyxTmuxAttachArguments(basis, socket, session, window)[^1]; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Is the terminal of this instance the one shown? The MAKYX tree marks its row.</summary>
    private bool IsInstanceShown(MakyxRun run) =>
        Selected is { Temporary: true } shown && MakyxAttachCommand(run) is { } command && shown.Profile.Command == command;

    private void DisposeMakyx() { _makyxClock?.Stop(); _makyx?.Dispose(); _makyx = null; }

    /// <summary>Palette ("makyx:ID" opens the page, "makyxterm:ID" the terminal); false for any other id.</summary>
    private bool RunMakyxPaletteItem(string id)
    {
        bool terminal = id.StartsWith("makyxterm:"), page = id.StartsWith("makyx:");
        if (!terminal && !page) return false;
        string uuid = id[(terminal ? "makyxterm:".Length : "makyx:".Length)..];
        var run = _makyx?.Runs().FirstOrDefault(r => r.Uuid == uuid);
        if (run is null) { StatusText.Text = "Dieser Auftrag ist nicht mehr in der Liste."; return true; }
        if (terminal) _ = OpenMakyxTerminalAsync(run); else OpenMakyxLink(run.Link);
        return true;
    }
}
