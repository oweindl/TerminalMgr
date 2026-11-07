using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>Makyx dashboard: the LÄUFE strip under KI-ABOS, a toast for new "waits for you" items, the jump into the Makyx page or the tmux window of an instance, and the palette entries.
/// Read only; nothing is changed in Makyx.</summary>
public partial class MainWindow
{
    private MakyxMonitor? _makyx;
    private string _makyxState = "";
    private DispatcherTimer? _makyxClock;
    private MakyxOptions Makyx => _settings.Makyx;

    private void InitializeMakyx()
    {
        MakyxRuns.OpenRequested += run => OpenMakyxLink(run.Link);
        MakyxRuns.CopyRequested += run => { try { Clipboard.SetText(run.Link); StatusText.Text = "Link kopiert."; } catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "Der Link ließ sich nicht kopieren."; } };
        MakyxRuns.TerminalRequested += run => _ = OpenMakyxTerminalAsync(run);
        MakyxRuns.TerminalProblem = TerminalProblem;
        _makyxClock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => { if (_makyx is not null && !Makyx.Collapsed) RenderMakyx(); }, Dispatcher);
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
        ApplyMakyxHeight();
        RenderMakyx();
    }

    private void OnMakyxChanged(MakyxMonitor monitor, MakyxChange change)
    {
        if (_closing || !ReferenceEquals(_makyx, monitor)) return;
        RenderMakyx();
        if (!Makyx.Notify) return;
        foreach (var item in change.NewWaiting)
            ToastWindow.Show("Wartet auf dich", $"{MakyxJson.WaitingKindText(item)}: {item.Title}" + (item.Project is { Length: > 0 } p ? " · " + p : ""), () => OpenMakyxLink(item.Link));
    }

    private void RenderMakyx()
    {
        if (MakyxRuns is null) return;
        var runs = _makyx?.Runs() ?? [];
        int waiting = _makyx?.Waiting().Count ?? 0;
        MakyxCount.Text = waiting > 0 ? waiting + " wartet" : runs.Count > 0 ? runs.Count.ToString() : "";
        MakyxCount.Foreground = waiting > 0 ? MakyxRunsPanel.DotBrush(MakyxStatus.Waiting) : (System.Windows.Media.Brush)FindResource("Text2Brush");
        MakyxStateLabel.Text = StateText();
        MakyxStateLabel.Visibility = MakyxStateLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string empty = !Makyx.Enabled ? "Makyx nicht eingerichtet · Einstellungen › Makyx-Leitstand" : _makyx is null ? "Makyx nicht verbunden." : "Keine Läufe.";
        MakyxRuns.Render(runs, empty);
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
        _settings = _settings with { Makyx = Makyx with { Collapsed = !Makyx.Collapsed } };
        ApplyMakyxHeight(); SaveSettings(); RenderMakyx();
    }

    private void MakyxMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "Neu verbinden", IsEnabled = Makyx.Enabled };
        refresh.Click += (_, _) => ApplyMakyxSettings(); menu.Items.Add(refresh);
        var settings = new MenuItem { Header = "Makyx-Einstellungen …" };
        settings.Click += (_, _) => OpenSettings(this, new RoutedEventArgs()); menu.Items.Add(settings);
        menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }

    private void ApplyMakyxHeight()
    {
        if (MakyxPanelRow is null || MakyxPanelBody is null) return;
        bool collapsed = Makyx.Collapsed;
        MakyxPanelRow.Height = collapsed ? new GridLength(36) : new GridLength(Math.Min(Makyx.Height, Math.Max(80, ServerContent.ActualHeight - 150)));
        MakyxPanelBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        MakyxCollapseButton.Content = (collapsed ? "›" : "⌄") + "  LÄUFE";
        MakyxScroll.MaxHeight = Math.Max(0, MakyxPanelRow.Height.Value - 62);
    }

    /// <summary>Only http and https are opened, in the default browser.</summary>
    private void OpenMakyxLink(string link)
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
            string name = "Makyx · " + window;
            var profile = basis with { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = StartMode.Command, Command = command, AutoConnect = false, AutoReconnect = false };
            var existing = _profiles.FirstOrDefault(p => p.Temporary && p.Profile.Name == name && p.Profile.Destination == basis.Destination);
            if (existing is not null) { ProfilesList.SelectedItem = existing; await ConnectAsync(existing); return; }
            var item = new ProfileItem(profile) { Temporary = true };
            _profiles.Add(item);
            ProfilesList.SelectedItem = item;
            await ConnectAsync(item);
        }
        catch (ArgumentException) { StatusText.Text = "Der tmux-Name von Makyx ist ungültig."; }
    }

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
