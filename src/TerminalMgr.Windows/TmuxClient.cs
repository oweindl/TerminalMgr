using TerminalMgr.Core;

namespace TerminalMgr.Windows;

public static class TmuxClient
{
    /// <summary>
    /// Runs a tmux command in the command prompt of the own client: prefix and ":" first, the command after a pause. The keys go to tmux,
    /// never to the program in the pane (which is why the pause matters: a one-shot write would leak characters into the program).
    /// </summary>
    public static async Task RunCommandAsync(PtySession session, string tmuxCommand, CancellationToken cancellationToken = default)
    {
        var steps = TmuxNavigation.Steps(tmuxCommand);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) await Task.Delay(TmuxWindowParser.StepDelay, cancellationToken);
            await session.SendAsync(steps[i]);
        }
    }

    /// <summary>Moves the tmux client running in <paramref name="session"/> to a window, even in another tmux session.</summary>
    public static async Task SwitchToWindowAsync(PtySession session, string sessionId, string windowId, CancellationToken cancellationToken = default)
    {
        var steps = TmuxWindowParser.SwitchSteps(sessionId, windowId);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) await Task.Delay(TmuxWindowParser.StepDelay, cancellationToken);
            await session.SendAsync(steps[i]);
        }
    }
}
