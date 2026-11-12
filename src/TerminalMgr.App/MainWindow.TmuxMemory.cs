using System.Windows.Threading;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

/// <summary>
/// Remembering the tmux sessions of a server (note 17, order H). The server keeps a file in its home directory with the sessions and window names
/// (see <see cref="TmuxSnapshot"/>); TerminalMgr only tells it when to write (after a change, at most once a minute, plus a refresh every ten minutes).
/// When a server is connected and sessions from the file are missing (typically after a restart), TerminalMgr asks before connecting: restore,
/// start fresh or ask later. Servers that can not be asked without a password dialog are skipped silently.
/// </summary>
public partial class MainWindow
{
    private readonly TmuxSnapshotPolicy _snapshotPolicy = new();
    private readonly HashSet<string> _snapshotRunning = [];
    private DispatcherTimer? _snapshotTimer;

    /// <summary>Only servers on the normal tmux, with the option on (the default); never an entry on the socket of Makyx.</summary>
    private static bool RemembersTmux(ProfileItem item) => !item.Temporary && item.TmuxSocket is null && item.Profile.RememberTmux;

    private void InitializeTmuxMemory()
    {
        // The refresh for servers that are connected but not shown (their strip does not run) and the pending snapshots.
        _snapshotTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => FlushSnapshots(), Dispatcher);
        _snapshotTimer.Start();
    }

    /// <summary>A frame of the window strip: the windows of the shown session decide whether the snapshot is outdated.</summary>
    private void ObserveTmuxWindows(ProfileItem item, IReadOnlyList<WindowCard> cards)
    {
        if (!RemembersTmux(item)) return;
        _snapshotPolicy.Observe(item.Profile.Id, TmuxSnapshot.Signature(cards.Select(c => (c.WindowId, c.Index, c.Name))));
        FlushSnapshots();
    }

    private void FlushSnapshots()
    {
        if (_closing) return;
        var now = DateTimeOffset.Now;
        foreach (var item in _profiles.Where(p => RemembersTmux(p) && p.Connection == ConnectionState.Connected && _sessions.ContainsKey(p.Profile.Id)).ToList())
        {
            string id = item.Profile.Id;
            if (_connecting.Contains(id) || !_snapshotPolicy.Due(id, now) || !_snapshotRunning.Add(id)) continue;
            _ = WriteSnapshotAsync(item, _snapshotPolicy.Current(id));
        }
    }

    private async Task WriteSnapshotAsync(ProfileItem item, string signature)
    {
        string id = item.Profile.Id;
        try { await _tmuxQuery.WriteSnapshotAsync(item.Profile, _shutdown.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // A server that can not be asked without a password, or without tmux: no error message, the next try comes with the refresh.
        }
        finally
        {
            _snapshotPolicy.Written(id, signature, DateTimeOffset.Now);
            _snapshotRunning.Remove(id);
        }
    }

    /// <summary>
    /// The check before connecting: is there a saved list with sessions that are missing on the server? With <paramref name="ask"/> the user decides;
    /// an automatic reconnect only protects the file (no snapshot is written until the question was answered). Returns the session to attach to
    /// after a restore, or null.
    /// </summary>
    private async Task<string?> OfferTmuxRestoreAsync(ProfileItem item, bool ask)
    {
        string id = item.Profile.Id;
        bool restoring = false;
        try
        {
            StatusText.Text = item.Name + ": prüfe gemerkte tmux-Sitzungen …";
            var check = await _tmuxQuery.CheckMemoryAsync(item.Profile, TimeSpan.FromSeconds(6), _shutdown.Token);
            var missing = TmuxSnapshot.Missing(check.Saved, check.Running);
            if (missing.Count == 0) { _snapshotPolicy.Resume(id); return null; }
            if (!ask)
            {
                _snapshotPolicy.Suspend(id);
                StatusText.Text = $"{item.Name}: gemerkte tmux-Sitzungen fehlen; beim nächsten Verbinden werde ich fragen.";
                return null;
            }
            var dialog = new TmuxRestoreDialog(item.Name, missing) { Owner = this };
            dialog.ShowDialog();
            switch (dialog.Choice)
            {
                case TmuxRestoreChoice.Restore:
                    restoring = true;
                    await _tmuxQuery.RestoreSessionsAsync(item.Profile, missing, _shutdown.Token);
                    _snapshotPolicy.Resume(id);
                    StatusText.Text = $"{item.Name}: {missing.Count} tmux-{(missing.Count == 1 ? "Session" : "Sessions")} wiederhergestellt.";
                    // The first session, or "main" when it was among them; a name tmux may have but TerminalMgr does not pass on is left to the profile.
                    return missing.Select(s => s.Name).OrderBy(n => n == "main" ? 0 : 1).FirstOrDefault(TmuxNaming.IsValid);
                case TmuxRestoreChoice.StartFresh:
                    _snapshotPolicy.Resume(id);   // the next snapshot replaces the file
                    return null;
                default:
                    _snapshotPolicy.Suspend(id);  // "Später fragen": the file stays as it is
                    return null;
            }
        }
        catch (OperationCanceledException) when (_closing) { return null; }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // No key login for the query, or the server does not answer: nothing to offer. A restore that failed half-way is told.
            if (!restoring) StatusText.Text = "";
            else StatusText.Text = $"{item.Name}: tmux-Sitzungen konnten nicht wiederhergestellt werden: {ex.Message.Split('\n')[0].Trim()}";
            return null;
        }
    }
}
