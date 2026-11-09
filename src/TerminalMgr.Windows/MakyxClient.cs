using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <summary>Reads the Makyx dashboard API (read only). One HttpClient; the CA of the runner channel is pinned by fingerprint, the token goes in the Authorization header and nowhere else.</summary>
public sealed class MakyxClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public MakyxClient(MakyxOptions options, string token)
    {
        if (!options.TryBaseUri(out var uri)) throw new ArgumentException(options.Problem() ?? "Adresse ungültig.");
        _base = uri!;
        _http = new HttpClient(CreateHandler(options.CaFingerprint)) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The connection handler: no redirects, no proxy, and the CA of the runner channel pinned by its fingerprint.</summary>
    private static SocketsHttpHandler CreateHandler(string pin) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false,
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, chain, errors) => errors == SslPolicyErrors.None || MakyxTls.Validate(cert as X509Certificate2 ?? (cert is null ? null : new X509Certificate2(cert)), chain, errors, pin)
        }
    };

    /// <summary>
    /// Exchanges the one-time code of a configuration text for the token: POST …/kopplung with {"code": "..."} and the answer {"token": "mkl_..."}.
    /// There is no token yet, so the call carries none; the code is the credential. Neither code nor token ever appears in a message.
    /// </summary>
    public static async Task<string> RedeemAsync(MakyxOptions options, string code, CancellationToken cancel)
    {
        if (!options.TryBaseUri(out var baseUri)) throw new ArgumentException(options.Problem() ?? "Adresse ungültig.");
        using var http = new HttpClient(CreateHandler(options.CaFingerprint)) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(baseUri!, "kopplung"), content, cancel);
            if (response.StatusCode is HttpStatusCode.Gone) throw new MakyxException("Der Code ist abgelaufen. In Makyx unter Leitstand-Zugänge die Konfiguration neu erzeugen.", response.StatusCode);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new MakyxException("Der Code ist ungültig oder wurde schon benutzt. In Makyx unter Leitstand-Zugänge die Konfiguration neu erzeugen.", response.StatusCode);
            if (response.StatusCode is HttpStatusCode.TooManyRequests) throw new MakyxException("Makyx begrenzt die Abfragen; bitte in einer Minute noch einmal versuchen.", response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new MakyxException($"Makyx antwortet mit {(int)response.StatusCode}.", response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(cancel);
            if (body.Length > 20_000) throw new MakyxException("Makyx antwortet nicht wie erwartet.");
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("token", out var tokenElement)
                    && tokenElement.ValueKind == System.Text.Json.JsonValueKind.String && tokenElement.GetString() is { } token
                    && token.StartsWith("mkl_", StringComparison.Ordinal) && token.Length <= 100 && !token.Any(char.IsWhiteSpace))
                    return token;
            }
            catch (System.Text.Json.JsonException) { /* handled below */ }
            throw new MakyxException("Makyx hat kein Token geliefert (unerwartete Antwort).");
        }
        catch (HttpRequestException ex) { throw new MakyxException(Describe(ex)); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new MakyxException("Makyx antwortet nicht (Zeitlimit)."); }
    }

    public sealed class MakyxException(string message, HttpStatusCode? status = null) : Exception(message) { public HttpStatusCode? Status { get; } = status; }

    private async Task<string> GetAsync(string path, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.GetAsync(new Uri(_base, path), HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized) throw new MakyxException("Das Token wurde nicht angenommen (falsch oder widerrufen).", response.StatusCode);
            if (response.StatusCode is HttpStatusCode.TooManyRequests) throw new MakyxException("Makyx begrenzt die Abfragen; später erneut versuchen.", response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new MakyxException($"Makyx antwortet mit {(int)response.StatusCode}.", response.StatusCode);
            return await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (HttpRequestException ex) { throw new MakyxException(Describe(ex)); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new MakyxException("Makyx antwortet nicht (Zeitlimit)."); }
    }

    private static string Describe(HttpRequestException ex) =>
        ex.InnerException is System.Security.Authentication.AuthenticationException ? "Die Zertifikatsprüfung ist fehlgeschlagen (CA-Fingerprint oder Servername stimmt nicht)." : "Makyx ist nicht erreichbar.";

    public async Task<MakyxHello> HelloAsync(CancellationToken cancel) => MakyxJson.ParseHello(await GetAsync("", cancel));
    public async Task<(IReadOnlyList<MakyxRun> Runs, DateTimeOffset? Time)> RunsAsync(DateTimeOffset? since, CancellationToken cancel)
    {
        string path = "laeufe?limit=100" + (since is { } s ? "&seit=" + Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")) : "");
        var runs = MakyxJson.ParseRuns(await GetAsync(path, cancel), out var time);
        return (runs, time);
    }
    public async Task<IReadOnlyList<MakyxWaiting>> WaitingAsync(CancellationToken cancel) => MakyxJson.ParseWaiting(await GetAsync("wartet", cancel));
    public async Task<IReadOnlyList<MakyxMachine>> MachinesAsync(CancellationToken cancel) => MakyxJson.ParseMachines(await GetAsync("rechner", cancel));

    /// <summary>A one-time sign-in code for the Makyx web interface (POST anmeldecode, valid about 60 s); the code is never logged.</summary>
    public async Task<string> LoginCodeAsync(CancellationToken cancel)
    {
        string body = await PostAsync("anmeldecode", "{}", cancel);
        try { return MakyxJson.ParseLoginCode(body) ?? throw new MakyxException("Makyx hat keinen Anmeldecode geliefert."); }
        catch (System.Text.Json.JsonException) { throw new MakyxException("Makyx antwortet nicht wie erwartet."); }
    }

    public async Task<IReadOnlyList<MakyxProject>> ProjectsAsync(CancellationToken cancel) => MakyxJson.ParseProjects(await GetAsync("projekte", cancel));

    // Writing actions (right "steuern"); Makyx logs each with the access. Ids are checked before they go into a path.
    /// <summary>Starts a terminal session; the answer is the session (with terminal) when Makyx returns it, else null.</summary>
    public async Task<MakyxRun?> StartSessionAsync(string project, string? machine, string? agent, string? model, CancellationToken cancel,
        string? name = null, MakyxFolder? folder = null, string? firstMessage = null)
    {
        string body = await PostAsync("sessions", MakyxControl.SessionBody(project, machine, agent, model, name, folder, firstMessage), cancel);
        try { return MakyxJson.ParseStarted(body); } catch (System.Text.Json.JsonException) { return null; }
    }

    public Task AnswerAsync(string waitingId, MakyxDecision decision, string? text, CancellationToken cancel) =>
        PostAsync("wartet/" + MakyxControl.PathId(waitingId) + "/antwort", MakyxControl.AnswerBody(decision, text), cancel);

    public Task StartRunAsync(string project, IReadOnlyList<string> items, CancellationToken cancel) =>
        PostAsync("laeufe", MakyxControl.StartRunBody(project, items), cancel);

    public Task StopRunAsync(string runId, CancellationToken cancel) => PostAsync("laeufe/" + MakyxControl.PathId(runId) + "/anhalten", "{}", cancel);

    public Task EmergencyStopAsync(string runId, CancellationToken cancel) => PostAsync("laeufe/" + MakyxControl.PathId(runId) + "/nothalt", MakyxControl.EmergencyBody, cancel);

    /// <summary>Renames a session (PATCH sessions/{id}, Makyx-PoC/17); Makyx renames its tmux window with it.</summary>
    public Task RenameSessionAsync(string sessionId, string name, CancellationToken cancel) =>
        SendAsync(HttpMethod.Patch, "sessions/" + MakyxControl.PathId(sessionId), MakyxControl.RenameBody(name), cancel);

    private Task<string> PostAsync(string path, string json, CancellationToken cancel) => SendAsync(HttpMethod.Post, path, json, cancel);

    private async Task<string> SendAsync(HttpMethod method, string path, string json, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized) throw new MakyxException("Das Token wurde nicht angenommen (falsch oder widerrufen).", response.StatusCode);
            if (response.StatusCode is HttpStatusCode.TooManyRequests) throw new MakyxException("Makyx begrenzt die Abfragen; später erneut versuchen.", response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) throw new MakyxException(MakyxControl.ErrorText((int)response.StatusCode, body) + ".", response.StatusCode);
            if (body.Length > 100_000) throw new MakyxException("Makyx antwortet nicht wie erwartet.");
            return body;
        }
        catch (HttpRequestException ex) { throw new MakyxException(Describe(ex)); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new MakyxException("Makyx antwortet nicht (Zeitlimit)."); }
    }

    /// <summary>The event stream until it ends or <paramref name="cancel"/> fires. Throws <see cref="MakyxException"/> when it can not be opened.</summary>
    public async IAsyncEnumerable<SseEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "ereignisse"));
            request.Headers.Accept.Clear(); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        catch (HttpRequestException ex) { throw new MakyxException(Describe(ex)); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized) throw new MakyxException("Das Token wurde nicht angenommen (falsch oder widerrufen).", response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new MakyxException($"Der Ereignisstrom antwortet mit {(int)response.StatusCode}.", response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            var parser = new SseParser();
            var decoder = Encoding.UTF8.GetDecoder();
            var buffer = new byte[4096];
            var chars = new char[8192];
            while (!cancel.IsCancellationRequested)
            {
                // The server sends a keepalive every 15 s; 60 s of silence means the connection is dead.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try { n = await stream.ReadAsync(buffer, idle.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new MakyxException("Der Ereignisstrom ist verstummt."); }
                catch (IOException) { throw new MakyxException("Der Ereignisstrom wurde unterbrochen."); }
                if (n == 0) yield break;
                int count = decoder.GetChars(buffer, 0, n, chars, 0);
                foreach (var e in parser.Feed(new string(chars, 0, count))) yield return e;
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Keeps the dashboard up to date: snapshot, then the event stream; after a break it reconnects (2/4/8/16/30 s, no limit) and catches up with <c>seit</c>.</summary>
public sealed class MakyxMonitor : IDisposable
{
    private readonly MakyxOptions _options;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    public MakyxState State { get; } = new();
    /// <summary>Raised from a background thread after every change; <c>NewWaiting</c> lists what waits for the user and was not known before.</summary>
    public event Action<MakyxChange>? Changed;
    /// <summary>"connected", "connecting" or an error text; with the time of the last good contact.</summary>
    public event Action<string, DateTimeOffset?>? ConnectionChanged;
    public DateTimeOffset? LastContact { get; private set; }
    private Task? _loop;
    private readonly object _gate = new();

    public MakyxMonitor(MakyxOptions options, string token) { _options = options; _token = token; }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancel)
    {
        int failures = 0;
        bool first = true;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var client = new MakyxClient(_options, _token);
                ConnectionChanged?.Invoke("connecting", LastContact);
                var since = first ? null : State.LastTime;
                var runs = await client.RunsAsync(since, cancel);
                var waiting = await client.WaitingAsync(cancel);
                MakyxChange change;
                lock (_gate)
                {
                    if (first || since is null) change = State.LoadSnapshot(runs.Runs, waiting, runs.Time);
                    else { State.MergeRuns(runs.Runs, runs.Time); change = State.LoadSnapshot([], waiting, runs.Time); }
                }
                // The machines (tmux socket, SSH target) let the server menu list Makyx windows; an old Makyx without /rechner is no error.
                try { var machines = await client.MachinesAsync(cancel); lock (_gate) _machines = machines; }
                catch (MakyxClient.MakyxException) { }
                // Projects (menu "Projekte"); an older Makyx without /projekte is no error either.
                try { var projects = await client.ProjectsAsync(cancel); lock (_gate) _projects = projects; }
                catch (Exception ex) when (ex is MakyxClient.MakyxException or System.Text.Json.JsonException) { }
                first = false; failures = 0; LastContact = DateTimeOffset.Now;
                ConnectionChanged?.Invoke("connected", LastContact);
                Changed?.Invoke(change);
                await foreach (var e in client.StreamAsync(cancel))
                {
                    LastContact = DateTimeOffset.Now;
                    MakyxChange c;
                    lock (_gate) c = State.Apply(e);
                    if (c.Changed) Changed?.Invoke(c);
                }
                ConnectionChanged?.Invoke("Der Ereignisstrom wurde beendet; verbinde neu.", LastContact);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (MakyxClient.MakyxException ex)
            {
                failures++;
                ConnectionChanged?.Invoke(ex.Message, LastContact);
                if (ex.Status == HttpStatusCode.Unauthorized) { await Task.Delay(TimeSpan.FromSeconds(60), cancel).ContinueWith(_ => { }); continue; }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
            {
                failures++;
                ConnectionChanged?.Invoke("Unerwartete Antwort von Makyx.", LastContact);
            }
            int wait = new[] { 2, 4, 8, 16, 30 }[Math.Min(Math.Max(failures, 1), 5) - 1];
            try { await Task.Delay(TimeSpan.FromSeconds(wait), cancel); } catch (OperationCanceledException) { return; }
        }
    }

    public IReadOnlyList<MakyxRun> Runs() { lock (_gate) return State.Ordered(); }
    public IReadOnlyCollection<MakyxWaiting> Waiting() { lock (_gate) return State.Waiting.ToList(); }
    private IReadOnlyList<MakyxMachine> _machines = [];
    /// <summary>The machines as of the last (re)connect.</summary>
    public IReadOnlyList<MakyxMachine> Machines() { lock (_gate) return _machines; }
    private IReadOnlyList<MakyxProject> _projects = [];
    /// <summary>The projects as of the last (re)connect or <see cref="RefreshProjectsAsync"/>.</summary>
    public IReadOnlyList<MakyxProject> Projects() { lock (_gate) return _projects; }

    /// <summary>Reloads the projects (counts of running instances and open gates change often); keeps the old list on any error.</summary>
    public async Task RefreshProjectsAsync()
    {
        try
        {
            using var client = new MakyxClient(_options, _token);
            var projects = await client.ProjectsAsync(_stop.Token);
            lock (_gate) _projects = projects;
        }
        catch (Exception ex) when (ex is MakyxClient.MakyxException or System.Text.Json.JsonException or OperationCanceledException or ObjectDisposedException or ArgumentException) { }
    }
    public void Dispose() { _stop.Cancel(); try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } _stop.Dispose(); }
}
