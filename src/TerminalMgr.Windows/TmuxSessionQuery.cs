using System.Diagnostics;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

public sealed record TmuxSessionInfo(string Id, string Name, int Windows, int Attached)
{
    public string Detail => $"{Windows} Fenster · {Attached} aktive Verbindung(en)";
}

/// <summary>A separate noninteractive SSH command leaves the current terminal untouched.</summary>
public sealed class TmuxSessionQuery
{
    public static string SshExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");

    public async Task<IReadOnlyList<TmuxSessionInfo>> ListAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        string stdout = await RunAsync(SshCommand.TmuxListArguments(profile), TimeSpan.FromSeconds(15), cancellationToken);
        return TmuxSessionParser.Parse(stdout).Select(s => new TmuxSessionInfo(s.Id, s.Name, s.Windows, s.Attached)).ToList();
    }

    /// <summary>All windows of all sessions. Works whether or not the server is connected in TerminalMgr.</summary>
    public async Task<IReadOnlyList<TmuxWindowRecord>> ListWindowsAsync(Profile profile, TimeSpan? timeout = null, CancellationToken cancellationToken = default, string? socket = null) =>
        TmuxWindowParser.Parse(await RunAsync(SshCommand.TmuxWindowsArguments(profile, socket), timeout ?? TimeSpan.FromSeconds(10), cancellationToken));

    /// <summary>Asks the server to write its snapshot of the tmux sessions (see <see cref="SshCommand.TmuxSnapshotArguments"/>); nothing comes back.</summary>
    public Task WriteSnapshotAsync(Profile profile, CancellationToken cancellationToken = default) =>
        RunAsync(SshCommand.TmuxSnapshotArguments(profile), TimeSpan.FromSeconds(20), cancellationToken);

    /// <summary>The saved file and the running sessions of the server in one answer; throws when the server can not be asked without a password dialog.</summary>
    public async Task<TmuxMemoryCheck> CheckMemoryAsync(Profile profile, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        TmuxSnapshot.ParseCheck(await RunAsync(SshCommand.TmuxMemoryCheckArguments(profile), timeout ?? TimeSpan.FromSeconds(8), cancellationToken));

    /// <summary>Creates the missing sessions with their windows (see <see cref="SshCommand.TmuxRestoreArguments"/>).</summary>
    public Task RestoreSessionsAsync(Profile profile, IReadOnlyList<SavedTmuxSession> sessions, CancellationToken cancellationToken = default) =>
        RunAsync(SshCommand.TmuxRestoreArguments(profile, sessions), TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Renames a tmux window by its ID (see <see cref="SshCommand.TmuxRenameWindowArguments"/>).</summary>
    public Task RenameWindowAsync(Profile profile, string windowId, string name, string? socket = null, CancellationToken cancellationToken = default) =>
        RunAsync(SshCommand.TmuxRenameWindowArguments(profile, windowId, name, socket), TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>Closes a tmux window by its ID (see <see cref="SshCommand.TmuxKillWindowArguments"/>); the programs in it end.</summary>
    public Task KillWindowAsync(Profile profile, string windowId, string? socket = null, CancellationToken cancellationToken = default) =>
        RunAsync(SshCommand.TmuxKillWindowArguments(profile, windowId, socket), TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>Ends a Makyx instance by its tmux window (see <see cref="SshCommand.MakyxTmuxEndArguments"/>); takes a few seconds.</summary>
    public Task EndMakyxWindowAsync(Profile profile, string socket, string session, string window, CancellationToken cancellationToken = default) =>
        RunAsync(SshCommand.MakyxTmuxEndArguments(profile, socket, session, window), TimeSpan.FromSeconds(25), cancellationToken);

    /// <summary>One frame: the screen of every window of one session; <paramref name="sessionTarget"/> is a session ID ($1) or name.</summary>
    public async Task<WindowPoll> StripAsync(Profile profile, string sessionTarget, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WindowStripParser.Parse(await RunAsync(SshCommand.TmuxStripArguments(profile, sessionTarget), timeout ?? TimeSpan.FromSeconds(8), cancellationToken));

    /// <summary>Output of "ssh -G" (the effective settings, no connection is made), or null when ssh does not answer within 5 s.</summary>
    public async Task<string?> EffectiveConfigAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(SshExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in SshCommand.EffectiveConfigArguments(profile)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ssh konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); return null; }
        return await output;
    }

    /// <summary>The RemoteCommand OpenSSH would use for this profile, or null.</summary>
    public async Task<string?> RemoteCommandAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        string? effective = await EffectiveConfigAsync(profile, cancellationToken);
        if (effective is null) return null;
        foreach (string line in effective.Split('\n'))
            if (line.StartsWith("remotecommand ", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["remotecommand ".Length..].Trim();
                return value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;
            }
        return null;
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(SshExecutable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Der Server hat innerhalb von {limit.TotalSeconds:0} Sekunden nicht geantwortet.");
        }
        string stdout = await output;
        string stderr = await error;
        if (process.ExitCode != 0)
        {
            if (stderr.Contains("no server running", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("no sessions", StringComparison.OrdinalIgnoreCase)) return "";
            throw new InvalidOperationException(
                "tmux-Abfrage fehlgeschlagen. Die Abfrage nutzt SSH-Key/Agent ohne Passwortdialog.\n" + stderr.Trim());
        }
        return stdout;
    }
}
