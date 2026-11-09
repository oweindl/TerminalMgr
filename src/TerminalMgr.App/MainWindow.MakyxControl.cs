using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// Steering Makyx from TerminalMgr (note 15, stages 2 and 3; Makyx-PoC/13 B and C): projects in the ⋯ menu with "Neue Session" and
/// "Lauf starten", answers to gates and questions, stopping a run and the emergency stop. Every action needs an access with the right
/// "steuern"; Makyx logs it with that access. Actions that cannot be undone ask first.
/// </summary>
public partial class MainWindow
{
    /// <summary>The session started from here; its terminal opens as soon as Makyx reports its tmux window.</summary>
    private string? _makyxPendingSession;

    private void InitializeMakyxControl()
    {
        MakyxRuns.StopRequested += run => _ = StopMakyxRunAsync(run, emergency: false);
        MakyxRuns.EmergencyRequested += run => _ = StopMakyxRunAsync(run, emergency: true);
        // The MAKYX tree: a new session preselects the machine of its branch; a gate opens its own choice in the Makyx page.
        MakyxRuns.NewSessionRequested += (project, machine) =>
            _ = StartMakyxSessionAsync(machine is null ? project : project with { Machine = MakyxControl.MachineValue(machine) });
        MakyxRuns.RunRequested += project => _ = StartMakyxRunAsync(project);
        MakyxRuns.ProjectPageRequested += project => OpenMakyxLink(project.Link);
        MakyxRuns.AnswerRequested += item =>
        {
            if (item.Kind == "rueckfrage") _ = AnswerMakyxAsync(item, MakyxDecision.Return);
            else OpenMakyxLink(item.Link);
        };
    }

    private bool AskMakyx(string question) =>
        MessageBox.Show(this, question, "Makyx", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Runs one writing action with the paired token; errors (no right "steuern", unknown entry …) are shown in a message box.</summary>
    private async Task<bool> MakyxActionAsync(string doing, Func<MakyxClient, Task> action, string done)
    {
        string? token = null;
        if (Makyx.Enabled && Makyx.Problem() is null)
            try { token = MakyxCredentialStore.Read(); } catch (System.ComponentModel.Win32Exception) { }
        if (token is null) { StatusText.Text = "Makyx ist nicht eingerichtet (Einstellungen › Makyx-Leitstand)."; return false; }
        StatusText.Text = doing + " …";
        try
        {
            using var client = new MakyxClient(Makyx, token);
            await action(client);
            StatusText.Text = done;
            if (_makyx is { } monitor) _ = monitor.RefreshProjectsAsync();
            return true;
        }
        catch (Exception ex) when (ex is MakyxClient.MakyxException or ArgumentException)
        {
            StatusText.Text = "Makyx: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Makyx", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    /// <summary>The "Projekte" and "Wartet" entries of the ⋯ menu.</summary>
    private void AddMakyxControlMenus(ContextMenu menu)
    {
        if (_makyx is not { } monitor) return;
        _ = monitor.RefreshProjectsAsync();   // counts for the next opening
        var projects = monitor.Projects();
        var projectsItem = new MenuItem { Header = projects.Count == 0 ? "Projekte (keine gemeldet)" : "Projekte", IsEnabled = projects.Count > 0 };
        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var entry = new MenuItem { Header = MakyxControl.ProjectText(project) };
            var page = new MenuItem { Header = "Projektseite öffnen", IsEnabled = project.Link.Length > 0 };
            page.Click += (_, _) => OpenMakyxLink(project.Link);
            var session = new MenuItem { Header = "Neue Session …" };
            session.Click += (_, _) => _ = StartMakyxSessionAsync(project);
            var run = new MenuItem { Header = "Lauf starten …" };
            run.Click += (_, _) => _ = StartMakyxRunAsync(project);
            entry.Items.Add(page); entry.Items.Add(session); entry.Items.Add(run);
            projectsItem.Items.Add(entry);
        }
        menu.Items.Add(projectsItem);

        var waiting = monitor.Waiting().ToList();
        var waitingItem = new MenuItem { Header = waiting.Count == 0 ? "Wartet (nichts)" : $"Wartet ({waiting.Count})", IsEnabled = waiting.Count > 0 };
        foreach (var item in waiting)
        {
            var entry = new MenuItem { Header = $"{MakyxJson.WaitingKindText(item)}: {item.Title}" + (item.Project is { Length: > 0 } p ? " · " + p : "") };
            var open = new MenuItem { Header = "In Makyx öffnen", IsEnabled = item.Link.Length > 0 };
            open.Click += (_, _) => OpenMakyxLink(item.Link);
            entry.Items.Add(open);
            if (item.Kind == "entscheidung")
            {
                var approve = new MenuItem { Header = "Freigeben …" };
                approve.Click += (_, _) => _ = AnswerMakyxAsync(item, MakyxDecision.Approve);
                var reject = new MenuItem { Header = "Ablehnen …" };
                reject.Click += (_, _) => _ = AnswerMakyxAsync(item, MakyxDecision.Reject);
                var back = new MenuItem { Header = "Zurückgeben mit Antwort …" };
                back.Click += (_, _) => _ = AnswerMakyxAsync(item, MakyxDecision.Return);
                entry.Items.Add(approve); entry.Items.Add(reject); entry.Items.Add(back);
            }
            else if (item.Kind == "rueckfrage")
            {
                var answer = new MenuItem { Header = "Antworten …" };
                answer.Click += (_, _) => _ = AnswerMakyxAsync(item, MakyxDecision.Return);
                entry.Items.Add(answer);
            }
            waitingItem.Items.Add(entry);
        }
        menu.Items.Add(waitingItem);
    }

    private async Task StartMakyxSessionAsync(MakyxProject project)
    {
        var machines = (_makyx?.Machines() ?? []).GroupBy(MakyxControl.MachineValue, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var dialog = new MakyxSessionDialog(project, machines) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        MakyxRun? started = null;
        bool ok = await MakyxActionAsync("Session wird gestartet", async c => started = await c.StartSessionAsync(project.Id, dialog.Machine, dialog.Agent, dialog.Model, CancellationToken.None),
            "Session gestartet; das Terminal öffnet sich, sobald Makyx das tmux-Fenster meldet.");
        if (!ok) return;
        if (started is { Terminal.Available: true }) { await OpenMakyxTerminalAsync(started); return; }
        _makyxPendingSession = started?.Id;
    }

    /// <summary>Called after every change of the monitor: opens the terminal of the session started from here once it has one.</summary>
    private void OpenPendingMakyxSession()
    {
        if (_makyxPendingSession is not { } id || _makyx is null) return;
        var run = _makyx.Runs().FirstOrDefault(r => r.Id == id || r.Uuid == id);
        if (run is not { Terminal.Available: true }) return;
        _makyxPendingSession = null;
        _ = OpenMakyxTerminalAsync(run);
    }

    private async Task StartMakyxRunAsync(MakyxProject project)
    {
        var dialog = new MakyxTextDialog("Lauf starten · " + project.Name, "Punkte, einer je Zeile: Backlog-Kennung oder Text eines neuen Punkts.", "Lauf starten", required: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var items = MakyxControl.RunItems(dialog.Text);
        if (!AskMakyx($"Lauf in {project.Name} mit {items.Count} Punkt(en) starten?")) return;
        await MakyxActionAsync("Lauf wird gestartet", c => c.StartRunAsync(project.Id, items, CancellationToken.None), "Lauf gestartet; er erscheint unter MAKYX beim Projekt.");
    }

    private async Task AnswerMakyxAsync(MakyxWaiting item, MakyxDecision decision)
    {
        string? text = null;
        if (decision == MakyxDecision.Return)
        {
            var dialog = new MakyxTextDialog((item.Kind == "rueckfrage" ? "Antwort · " : "Zurückgeben · ") + item.Title, "Diese Antwort geht an den Agenten.", "Senden", required: true) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            text = dialog.Text;
        }
        else if (decision == MakyxDecision.Approve && !AskMakyx($"„{item.Title}“ freigeben?")) return;
        else if (decision == MakyxDecision.Reject && !AskMakyx($"„{item.Title}“ ablehnen? Der Lauf wird damit verworfen.")) return;
        await MakyxActionAsync("Antwort wird gesendet", c => c.AnswerAsync(item.Id, decision, text, CancellationToken.None),
            decision switch { MakyxDecision.Approve => "Freigegeben.", MakyxDecision.Reject => "Abgelehnt.", _ => "Antwort gesendet." });
    }

    private async Task StopMakyxRunAsync(MakyxRun run, bool emergency)
    {
        if (emergency)
        {
            if (!AskMakyx("Nothalt stoppt ganz Makyx: alle Läufe und Sessions. Aufheben geht nur in der Makyx-Weboberfläche.\n\nNothalt wirklich auslösen?")) return;
            await MakyxActionAsync("Nothalt wird ausgelöst", c => c.EmergencyStopAsync(run.Id, CancellationToken.None), "Nothalt ausgelöst. Aufheben in der Makyx-Weboberfläche.");
            return;
        }
        if (!AskMakyx($"{run.Id} „{run.Title}“ anhalten? Der Lauf wird abgebrochen und nicht gemergt.")) return;
        await MakyxActionAsync("Lauf wird angehalten", c => c.StopRunAsync(run.Id, CancellationToken.None), $"{run.Id} angehalten.");
    }
}
