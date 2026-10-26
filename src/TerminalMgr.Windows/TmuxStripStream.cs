using System.Diagnostics;
using System.Text;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <summary>
/// One long-lived, noninteractive SSH connection that runs the frame loop of <see cref="WindowStripParser.Script"/> on the server:
/// once a second the windows of one session with their screens. One connection instead of one SSH call per second; it uses key or
/// agent login like the other queries and never touches the programs in the windows. Dispose ends the connection, and with it the loop.
/// </summary>
public sealed class TmuxStripStream : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;

    private TmuxStripStream(Process process, Action<WindowPoll> onFrame, Action<string> onEnded)
    {
        _process = process;
        _reader = Task.Run(() => ReadAsync(onFrame, onEnded));
    }

    /// <summary>Starts the connection. <paramref name="onFrame"/> and <paramref name="onEnded"/> are called on a background thread.</summary>
    public static TmuxStripStream Start(Profile profile, string sessionTarget, Action<WindowPoll> onFrame, Action<string> onEnded, int intervalSeconds = 1)
    {
        var start = new ProcessStartInfo(TmuxSessionQuery.SshExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in SshCommand.TmuxStripArguments(profile, sessionTarget, intervalSeconds)) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("ssh konnte nicht gestartet werden.");
        return new TmuxStripStream(process, onFrame, onEnded);
    }

    private async Task ReadAsync(Action<WindowPoll> onFrame, Action<string> onEnded)
    {
        var frame = new StringBuilder();
        var error = _process.StandardError.ReadToEndAsync();
        const string reason = "Die Verbindung für die Fensterleiste wurde beendet.";
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync(_stop.Token)) is not null)
            {
                if (frame.Length > 4_000_000) frame.Clear();             // a runaway frame: start over
                frame.Append(line).Append('\n');
                if (line.TrimEnd('\r') != WindowStripParser.EndMarker) continue;
                var poll = WindowStripParser.Parse(frame.ToString());
                frame.Clear();
                if (poll.Windows.Count > 0) onFrame(poll);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (IOException) { }
        try { await _process.WaitForExitAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        string text = "";
        try { text = (await error.WaitAsync(TimeSpan.FromSeconds(1))).Trim(); } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        if (!_stop.IsCancellationRequested) onEnded(text.Length > 0 ? text.Split('\n')[0].Trim() : reason);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _process.Dispose();
        _stop.Dispose();
    }
}
