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
    public async Task<IReadOnlyList<TmuxWindowRecord>> ListWindowsAsync(Profile profile, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        TmuxWindowParser.Parse(await RunAsync(SshCommand.TmuxWindowsArguments(profile), timeout ?? TimeSpan.FromSeconds(10), cancellationToken));

    /// <summary>The RemoteCommand OpenSSH would use for this profile, or null.</summary>
    public async Task<string?> RemoteCommandAsync(Profile profile, CancellationToken cancellationToken = default)
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
        foreach (string line in (await output).Split('\n'))
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
