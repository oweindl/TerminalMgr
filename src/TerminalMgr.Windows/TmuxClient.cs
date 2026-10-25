using TerminalMgr.Core;

namespace TerminalMgr.Windows;

public static class TmuxClient
{
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
