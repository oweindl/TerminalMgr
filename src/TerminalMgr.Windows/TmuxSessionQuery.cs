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
        profile.Validate();
        var start = new ProcessStartInfo(SshExecutable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in SshCommand.TmuxListArguments(profile)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Der Server hat innerhalb von 15 Sekunden nicht geantwortet.");
        }
        string stdout = await output;
        string stderr = await error;
        if (process.ExitCode != 0)
        {
            if (stderr.Contains("no server running", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("no sessions", StringComparison.OrdinalIgnoreCase)) return [];
            throw new InvalidOperationException(
                "tmux-Abfrage fehlgeschlagen. Die Abfrage nutzt SSH-Key/Agent ohne Passwortdialog.\n" + stderr.Trim());
        }
        return TmuxSessionParser.Parse(stdout).Select(s => new TmuxSessionInfo(s.Id, s.Name, s.Windows, s.Attached)).ToList();
    }
}
