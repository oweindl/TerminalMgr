using System.Net;
using System.Net.Sockets;
using System.Text;
using TerminalMgr.Core;
using TerminalMgr.Windows;

/// <summary>The Makyx client against a small local HTTP server written with TcpListener (no admin rights, no certificate needed): token header, status codes, event stream, monitor with reconnect.</summary>
internal static class MakyxClientTests
{
    private sealed record Req(string Path, string Query, string? Authorization);

    /// <summary>Answers requests one by one; the handler returns status, content type and body.</summary>
    private sealed class FakeMakyx : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly List<Req> Requests = [];
        private readonly Func<Req, (int Status, string Type, string Body)> _handler;
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Address => $"http://127.0.0.1:{Port}";

        public FakeMakyx(Func<Req, (int, string, string)> handler)
        {
            _handler = handler;
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n")) { if (await stream.ReadAsync(one) == 0) return; head.Append((char)one[0]); }
                string[] lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                string[] first = lines[0].Split(' ');
                string target = first[1];
                int q = target.IndexOf('?');
                var req = new Req(q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?[14..].Trim());
                lock (Requests) Requests.Add(req);
                var (status, type, body) = _handler(req);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string header = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nConnection: close\r\n" + (type.StartsWith("text/event-stream") ? "" : $"Content-Length: {payload.Length}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                if (type.StartsWith("text/event-stream"))
                    foreach (var piece in body.Split('#')) { await stream.WriteAsync(Encoding.UTF8.GetBytes(piece)); await stream.FlushAsync(); await Task.Delay(20); }
                else await stream.WriteAsync(payload);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private const string Token = "mkl_testtoken_nicht_geheim";
    private static (int, string, string) Api(Req r) => r.Authorization != "Bearer " + Token ? (401, "application/json", """{"fehler":"token_ungueltig"}""") : r.Path switch
    {
        "/api/leitstand/v1/" => (200, "application/json", """{"schnittstelle":"v1","vault":"v","client":"Test","zeit":"2026-01-02T11:00:00Z"}"""),
        "/api/leitstand/v1/laeufe" => (200, "application/json", """{"laeufe":[{"id":"E-1","uuid":"u1","status":"arbeitet","titel":"t","projekt":"p","link":"http://127.0.0.1/x","terminal":{"vorhanden":false,"grund":"strukturiert"}}],"zeit":"2026-01-02T11:00:05Z"}"""),
        "/api/leitstand/v1/wartet" => (200, "application/json", """{"wartet":[{"id":"session:1","art":"session","unterart":"leerlauf","titel":"alt","link":"http://127.0.0.1/a"}]}"""),
        "/api/leitstand/v1/rechner" => (200, "application/json", """{"rechner":[{"id":"r","name":"rechner-a","status":"verbunden","verbunden":true}]}"""),
        _ => (404, "application/json", "{}")
    };

    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Makyx-Client: Token im Kopf, Antworten werden gelesen, 401 und 404 werden verständlich", () =>
        {
            using var server = new FakeMakyx(Api);
            var options = new MakyxOptions { Enabled = true, Address = server.Address };
            using var client = new MakyxClient(options, Token);
            var hello = client.HelloAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(hello.Client == "Test" && hello.Version == "v1", "Hallo");
            var (runs, time) = client.RunsAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            check(runs.Count == 1 && runs[0].Id == "E-1" && time is not null, "Läufe");
            check(client.WaitingAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 1 && client.MachinesAsync(CancellationToken.None).GetAwaiter().GetResult()[0].Name == "rechner-a", "Wartet und Rechner");
            lock (server.Requests) check(server.Requests.All(r => r.Authorization == "Bearer " + Token && !r.Path.Contains("mkl_") && !r.Query.Contains("mkl_")), "das Token steht nur im Authorization-Kopf, nie in der Adresse");
            using var wrong = new MakyxClient(options, "mkl_falsch");
            try { wrong.HelloAsync(CancellationToken.None).GetAwaiter().GetResult(); check(false, "falsches Token muss scheitern"); }
            catch (MakyxClient.MakyxException ex) { check(ex.Status == HttpStatusCode.Unauthorized && ex.Message.Contains("Token") && !ex.Message.Contains("mkl_"), "401: verständlich, ohne Token im Text"); }
            var since = DateTimeOffset.Parse("2026-01-02T11:00:00Z");
            client.RunsAsync(since, CancellationToken.None).GetAwaiter().GetResult();
            lock (server.Requests) check(server.Requests.Last().Query.Contains("seit=2026-01-02T11%3A00%3A00") && server.Requests.Last().Query.Contains("limit=100"), "seit wird als UTC-Zeit angehängt: " + server.Requests.Last().Query);
        });

        test("Makyx-Client: Ereignisstrom liefert Ereignisse in Stücken und endet mit dem Server", () =>
        {
            string body = "id: 1\nevent: hallo\ndata: {\"schnittstelle\":\"v1\"}\n\n#: keepalive\n\nid: 2\nevent: lauf\nda#ta: {\"id\":\"E-1\",\"uuid\":\"u1\",\"status\":\"fertig\"}\n\n";
            using var server = new FakeMakyx(r => r.Path.EndsWith("/ereignisse") ? (200, "text/event-stream", body) : Api(r));
            using var client = new MakyxClient(new MakyxOptions { Address = server.Address }, Token);
            var events = new List<SseEvent>();
            var task = Task.Run(async () => { await foreach (var e in client.StreamAsync(CancellationToken.None)) events.Add(e); });
            check(task.Wait(TimeSpan.FromSeconds(10)), "der Strom endet, wenn der Server schließt");
            check(events.Count == 2 && events[0].Name == "hallo" && events[1].Name == "lauf" && events[1].Data.Contains("E-1"), "zwei Ereignisse, mitten im Wort zerstückelt");
        });

        test("Makyx-Monitor: Bestand still, Neues gemeldet, nach Abbruch Wiederverbindung mit seit", () =>
        {
            int streams = 0;
            using var server = new FakeMakyx(r =>
            {
                if (!r.Path.EndsWith("/ereignisse")) return Api(r);
                int n = Interlocked.Increment(ref streams);
                return n == 1 ? (200, "text/event-stream", "id: 1\nevent: hallo\ndata: {}\n\n#id: 2\nevent: wartet\ndata: {\"id\":\"session:2\",\"art\":\"rueckfrage\",\"titel\":\"neu\",\"link\":\"http://127.0.0.1/b\"}\n\n")
                    : (200, "text/event-stream", "id: 1\nevent: hallo\ndata: {}\n\n");
            });
            using var monitor = new MakyxMonitor(new MakyxOptions { Address = server.Address }, Token);
            var announced = new List<string>();
            var states = new List<string>();
            monitor.Changed += c => { lock (announced) announced.AddRange(c.NewWaiting.Select(w => w.Id)); };
            monitor.ConnectionChanged += (s, _) => { lock (states) states.Add(s); };
            monitor.Start();
            var end = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < end)
            {
                lock (server.Requests) if (server.Requests.Count(r => r.Path.EndsWith("/ereignisse")) >= 2) break;
                Thread.Sleep(50);
            }
            Thread.Sleep(300);
            lock (announced) check(announced.SequenceEqual(["session:2"]), "nur das neue Wartende wird gemeldet, nicht der Bestand: " + string.Join(",", announced));
            lock (server.Requests)
            {
                check(server.Requests.Count(r => r.Path.EndsWith("/ereignisse")) >= 2, "nach dem Ende des Stroms wird neu verbunden");
                var catchUp = server.Requests.Where(r => r.Path.EndsWith("/laeufe")).Skip(1).FirstOrDefault();
                check(catchUp is not null && catchUp.Query.Contains("seit="), "die Wiederverbindung holt mit seit nach");
            }
            lock (states) check(states.Contains("connected") && states.Contains("connecting"), "Verbindungszustände werden gemeldet");
            check(monitor.Runs().Count == 1 && monitor.Waiting().Count == 1, "nach der Wiederverbindung gilt wieder der volle Stand des Servers");
        });

        test("Makyx-Monitor: falsches Token wird als Fehler gemeldet, nichts stürzt ab", () =>
        {
            using var server = new FakeMakyx(Api);
            using var monitor = new MakyxMonitor(new MakyxOptions { Address = server.Address }, "mkl_falsch");
            string? last = null;
            monitor.ConnectionChanged += (s, _) => last = s;
            monitor.Start();
            var end = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < end && (last is null || last == "connecting")) Thread.Sleep(50);
            check(last is not null && last.Contains("Token") && !last.Contains("mkl_"), "Fehlertext ohne Token: " + last);
        });
    }
}
