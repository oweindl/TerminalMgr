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
        string pin = options.CaFingerprint;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, chain, errors) => errors == SslPolicyErrors.None || MakyxTls.Validate(cert as X509Certificate2 ?? (cert is null ? null : new X509Certificate2(cert)), chain, errors, pin)
            }
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
    public void Dispose() { _stop.Cancel(); try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } _stop.Dispose(); }
}
