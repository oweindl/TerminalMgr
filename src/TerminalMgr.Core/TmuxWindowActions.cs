namespace TerminalMgr.Core;

/// <summary>What TerminalMgr says and checks before it changes a tmux window from the window strip.</summary>
public static class TmuxWindowActions
{
    /// <summary>
    /// The question before a window is closed: what runs in it is ended, and closing the last window ends the whole session.
    /// <paramref name="command"/> is the running program (<c>pane_current_command</c>), empty when unknown.
    /// </summary>
    public static string CloseQuestion(string window, string session, string? command, bool last)
    {
        string running = string.IsNullOrWhiteSpace(command) ? "Was darin läuft, wird beendet." : $"Was darin läuft ({command.Trim()}), wird beendet.";
        string text = $"Fenster „{window}“ in Session „{session}“ schließen? {running}";
        return last ? text + $" Damit endet die Session „{session}“." : text;
    }

    /// <summary>
    /// Entries on the own tmux socket belong to Makyx (it finds an instance by the window name, and ends it with "Session beenden"):
    /// the reason to show instead of an action, or null for the normal tmux.
    /// </summary>
    public static string? ManagedReason(string? socket) =>
        socket is null ? null : "von Makyx verwaltet – im Baum MAKYX: Session beenden";
}
