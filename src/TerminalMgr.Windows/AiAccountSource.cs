using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <summary>Read-only CLI connections, separate from the user's PTY. No model turns, login or token scraping.</summary>
public sealed class AiAccountSource : IAiAccountSource
{
    public async Task<IReadOnlyList<AiAccountSnapshot>> ReadAsync(Profile? profile, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(GuardAsync("Codex", () => ReadCodexAsync(profile, cancellationToken), cancellationToken),
            GuardAsync("Claude Code", () => ReadClaudeAsync(profile, cancellationToken), cancellationToken));
        return results;
    }

    private static async Task<AiAccountSnapshot> GuardAsync(string provider, Func<Task<AiAccountSnapshot>> read, CancellationToken token)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return AiAccountSnapshot.Unavailable(provider, "CLI nicht erreichbar, nicht installiert oder Abfrage nicht unterstützt") with { Error = "Abfrage fehlgeschlagen · Anmeldung / SSH-Key und CLI-Version prüfen" }; }
    }

    public static ProcessStartInfo Command(Profile? profile, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (profile is null)
        {
            start.FileName = executable;
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
        }
        else
        {
            profile.Validate();
            start.FileName = TmuxSessionQuery.SshExecutable;
            if (!string.IsNullOrWhiteSpace(profile.ConfigPath)) { start.ArgumentList.Add("-F"); start.ArgumentList.Add(profile.ConfigPath); }
            foreach (string arg in new[] { "-T", "-o", "RemoteCommand=none", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2", profile.Destination }) start.ArgumentList.Add(arg);
            // Login shell loads the user's CLI PATH. All values are single-quoted, not interpolated shell code.
            string command = "exec " + string.Join(" ", new[] { executable }.Concat(arguments).Select(SshCommand.ShellQuote));
            start.ArgumentList.Add("sh -lc " + SshCommand.ShellQuote(command));
        }
        return start;
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[4096];
        int length = 0;
        while (await reader.ReadAsync(buffer.AsMemory(), token) is var read && read > 0)
        {
            length += read;
            if (length > 1_048_576) throw new IOException("CLI-Ausgabe zu groß.");
        }
        return "";
    }

    public static async Task<string> RunAsync(ProcessStartInfo start, string? input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = Process.Start(start) ?? throw new IOException("CLI konnte nicht starten.");
        try
        {
            var errors = DrainAsync(process.StandardError, timeout.Token);
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors).WaitAsync(timeout.Token);
            // Claude auth status returns 1 when signed out, but still supplies a useful JSON response.
            string text = await output;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(text)) throw new IOException("CLI-Abfrage fehlgeschlagen.");
            return text;
        }
        finally { Stop(process); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token) is var read && read > 0)
        {
            if (output.Length + read > 1_048_576) throw new IOException("CLI-Ausgabe zu groß.");
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }
    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<AiAccountSnapshot> ReadCodexAsync(Profile? profile, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(Command(profile, "codex", "app-server")) ?? throw new IOException("Codex fehlt.");
        var token = timeout.Token;
        var errors = DrainAsync(process.StandardError, token);
        int sequence = 0;
        async Task<JsonElement> Rpc(string method, object parameters, bool optional = false)
        {
            int id = ++sequence;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(requestTimeout.Token);
                if (line is null) throw new IOException("Codex-Verbindung beendet.");
                if (line.Length > 1_048_576) throw new IOException("Codex-Ausgabe zu groß.");
                using var doc = JsonDocument.Parse(line);
                var response = doc.RootElement;
                var responseId = AiAccountParser.Get(response, "id");
                if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out int value) || value != id) continue;
                if (AiAccountParser.Get(response, "error").ValueKind == JsonValueKind.Object)
                {
                    if (optional) return default;
                    throw new IOException("Codex-Abfrage nicht unterstützt.");
                }
                return AiAccountParser.Get(response, "result").Clone();
            }
        }
        try
        {
            await Rpc("initialize", new { clientInfo = new { name = "terminalmgr", title = "TerminalMgr", version = typeof(AiAccountSource).Assembly.GetName().Version?.ToString() ?? "0.2.0" } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), token);
            var account = await Rpc("account/read", new { refreshToken = false });
            string? type = AiAccountParser.Text(AiAccountParser.Get(account, "account"), "type");
            JsonElement limits = default, usage = default, notices = default;
            if (type is "chatgpt" or "chatgptAuthTokens")
            {
                limits = await Rpc("account/rateLimits/read", new { }, optional: true);
                usage = await Rpc("account/usage/read", new { }, optional: true);
                notices = await Rpc("account/workspaceMessages/read", new { }, optional: true);
            }
            return AiAccountParser.Codex(account, limits, usage, notices, DateTimeOffset.UtcNow);
        }
        finally
        {
            process.StandardInput.Close();
            Stop(process);
            try { await errors; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    private static async Task<AiAccountSnapshot> ReadClaudeAsync(Profile? profile, CancellationToken token)
    {
        string text = await RunAsync(Command(profile, "claude", "auth", "status"), null, token);
        using var auth = JsonDocument.Parse(text);
        string snapshots;
        if (profile is null)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".terminalmgr", "ai", "claude");
            var all = new List<JsonElement>();
            if (Directory.Exists(directory)) foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
            {
                try { if (new FileInfo(file).Length <= 32768) { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, token)); all.Add(doc.RootElement.Clone()); } }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return AiAccountParser.Claude(auth.RootElement, all);
        }
        // Fixed path and bounded files; neither the credential store nor transcript files are accessed.
        const string readScript = "from pathlib import Path; import json; p=Path.home()/'.terminalmgr/ai/claude'; a=[]\nfor f in sorted(p.glob('*.json'),key=lambda f:f.stat().st_mtime,reverse=True)[:20]:\n try:\n  if f.stat().st_size<=32768: a.append(json.loads(f.read_text()))\n except (OSError,ValueError): pass\nprint(json.dumps(a))";
        try { snapshots = await RunAsync(Command(profile, "python3", "-c", readScript), null, token); }
        catch (Exception ex) when (ex is IOException or JsonException) { snapshots = "[]"; }
        using var collected = JsonDocument.Parse(snapshots);
        return AiAccountParser.Claude(auth.RootElement, collected.RootElement.EnumerateArray().Select(s => s.Clone()).ToArray());
    }

    public static async Task<AiAccountSnapshot> ReadOpenRouterAsync(string key, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) return AiAccountSnapshot.Unavailable("OpenRouter", $"Anmeldung / Zugriff prüfen (HTTP {(int)response.StatusCode})") with { Error = $"OpenRouter-Abfrage fehlgeschlagen (HTTP {(int)response.StatusCode})" };
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        using var doc = JsonDocument.Parse(await ReadBoundedAsync(reader, token));
        return AiAccountParser.OpenRouter(doc.RootElement, DateTimeOffset.UtcNow);
    }
}
