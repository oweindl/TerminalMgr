namespace TerminalMgr.Core;

/// <param name="Retry">Whether to connect again.</param>
/// <param name="Delay">Wait before the attempt.</param>
/// <param name="Attempt">Number of this attempt (1 is the first).</param>
/// <param name="Reason">Why not, when <paramref name="Retry"/> is false.</param>
public sealed record ReconnectDecision(bool Retry, TimeSpan Delay, int Attempt, string Reason = "");

/// <summary>
/// Decides what happens when an SSH connection ends on its own. Only a lost connection is retried; leaving the shell,
/// detaching from tmux or a failing remote command are normal ends. Waits grow from 2 s to 30 s.
/// </summary>
public sealed class ReconnectTracker
{
    /// <summary>OpenSSH reports connection errors (host unreachable, timeout, reset) as 255.</summary>
    public const int ConnectionLostExitCode = 255;
    /// <summary>Windows OpenSSH 9.5 reports "connection closed by remote host" as -1 (0xFFFFFFFF).</summary>
    public const int ConnectionClosedExitCode = -1;
    public static bool IsConnectionLost(int exitCode) => exitCode is ConnectionLostExitCode or ConnectionClosedExitCode;
    public const int MaxAttempts = 10;
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    private int _attempts;
    private DateTimeOffset? _connectedSince;

    public static TimeSpan DelayFor(int attempt) => TimeSpan.FromSeconds(attempt switch { <= 1 => 2, 2 => 4, 3 => 8, 4 => 16, _ => 30 });

    /// <summary>Call when the SSH process was started.</summary>
    public void Connected(DateTimeOffset now) => _connectedSince = now;

    /// <summary>Call for a wanted end of the user's work (disconnect, manual connect): the counter starts over.</summary>
    public void Reset() { _attempts = 0; _connectedSince = null; }

    public ReconnectDecision Lost(bool enabled, int exitCode, DateTimeOffset now)
    {
        if (!enabled) return new(false, TimeSpan.Zero, 0, "Automatische Wiederverbindung ist ausgeschaltet.");
        if (!IsConnectionLost(exitCode)) return new(false, TimeSpan.Zero, 0, "Die Sitzung wurde regulär beendet.");
        // A connection that held for a while was healthy: the next drop is a new incident, not another failed attempt.
        if (_connectedSince is { } since && now - since >= StableAfter) _attempts = 0;
        if (_attempts >= MaxAttempts) return new(false, TimeSpan.Zero, _attempts, $"Aufgegeben nach {MaxAttempts} Versuchen.");
        _attempts++;
        return new(true, DelayFor(_attempts), _attempts);
    }
}
