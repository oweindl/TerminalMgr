using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TerminalMgr.Core;

/// <summary>Makyx dashboard: API JSON, event stream, state, certificate pinning, tmux attach, profile matching, settings. Only neutral names (makyx.example, rechner-a, benutzer).</summary>
internal static class MakyxTests
{
    private const string RunsJson = """
    {"laeufe":[
      {"id":"E-018","uuid":"u-18","art":"einzelauftrag","nummer":18,"titel":"Kurzbericht","status":"fertig","phase":"fertig","projekt":"Beispiel","rechner":"rechner-a","beginn":"2026-01-02T10:00:00Z","ende":"2026-01-02T10:06:49Z","dauer_s":409,"kosten_usd":0.0121,"link":"https://makyx.example/v/m/auftrag/u-18","geaendert_am":"2026-01-02T10:06:49Z","instanzen":[],"terminal":{"vorhanden":false,"grund":"strukturiert"}},
      {"id":"E-019","uuid":"u-19","art":"einzelauftrag","titel":"Im Terminal","status":"wartet","projekt":"Beispiel","rechner":"rechner-a","beginn":"2026-01-02T11:00:00Z","ende":null,"dauer_s":60,"kosten_usd":null,"link":"https://makyx.example/v/m/auftrag/u-19","terminal":{"vorhanden":true,"rechner":"rechner-a","ssh_ziel":"benutzer@host-a","tmux_socket":"makyx","tmux_session":"makyx","tmux_fenster":"auftrag-19","befehl":"tmux -L makyx attach -t makyx"}},
      {"id":"L-001","uuid":"u-l1","art":"lauf","titel":"Lauf","status":"arbeitet","projekt":"Beispiel","link":"file:///etc/passwd","terminal":{"vorhanden":false,"grund":"keine_instanz"},"unbekanntes_feld":[1,2,3]}
    ],"zeit":"2026-01-02T11:01:00Z"}
    """;

    private static string Event(string name, string data, int id) => $"id: {id}\nevent: {name}\ndata: {data}\n\n";

    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Makyx: API-JSON wird zu Läufen, Status, Terminal und Kosten", () =>
        {
            var runs = MakyxJson.ParseRuns(RunsJson, out var time);
            check(runs.Count == 3 && time == DateTimeOffset.Parse("2026-01-02T11:01:00Z"), "drei Läufe und die Zeit der Antwort");
            check(runs[0].Id == "E-018" && runs[0].Status == MakyxStatus.Done && runs[0].DurationSeconds == 409 && runs[0].CostUsd == 0.0121 && !runs[0].Terminal.Available && runs[0].Terminal.Reason == "strukturiert", "fertiger Einzelauftrag ohne Terminal");
            check(runs[1].Status == MakyxStatus.Waiting && runs[1].IsActive && runs[1].CostUsd is null && runs[1].Terminal is { Available: true, TmuxSocket: "makyx", TmuxSession: "makyx", TmuxWindow: "auftrag-19", SshTarget: "benutzer@host-a" }, "wartender Auftrag mit tmux-Fenster, Kosten unbekannt (nicht 0)");
            check(runs[2].Link == "" && runs[2].Status == MakyxStatus.Working, "ein file:-Link wird nie übernommen");
            check(MakyxJson.ParseStatus("neu-erfunden") == MakyxStatus.Unknown && MakyxJson.ParseStatus(null) == MakyxStatus.Unknown, "unbekannter Status bleibt unbekannt");
            check(MakyxJson.SafeLink("javascript:alert(1)") == "" && MakyxJson.SafeLink("https://user:pw@makyx.example/") == "" && MakyxJson.SafeLink("http://makyx.example/x") != "", "nur http(s) ohne Zugangsdaten");
            check(MakyxJson.ParseRuns("""{"laeufe":[]}""", out _).Count == 0 && MakyxJson.ParseRuns("""{}""", out _).Count == 0, "leere Antworten sind keine Fehler");
            bool broken = false;
            try { MakyxJson.ParseRuns("{kaputt", out _); } catch (JsonException) { broken = true; }
            check(broken, "kaputtes JSON löst JsonException aus");
        });

        test("Makyx: Wartet-auf-dich und Rechner werden gelesen", () =>
        {
            var w = MakyxJson.ParseWaiting("""{"wartet":[{"id":"session:1","art":"rueckfrage","unterart":"berechtigung","titel":"E-019 Im Terminal","projekt":"Beispiel","seit":"2026-01-02T11:00:00Z","alter_s":300,"link":"https://makyx.example/a","bezug":"E-019"},{"id":"zertifikat:x","art":"zertifikat","unterart":"fehler","titel":"Zertifikat von rechner-a","projekt":null,"seit":null,"alter_s":null,"link":"https://makyx.example/r","bezug":"rechner-a"}]}""");
            check(w.Count == 2 && w[0].AgeSeconds == 300 && MakyxJson.WaitingKindText(w[0]) == "Rückfrage" && MakyxJson.WaitingKindText(w[1]) == "Zertifikat" && w[1].Project is null, "zwei Einträge mit Art und Alter");
            var m = MakyxJson.ParseMachines("""{"rechner":[{"id":"r1","name":"rechner-a","status":"verbunden","verbunden":true,"version":"0.9.2","ssh_ziel":"benutzer@host-a","tmux":{"socket":"makyx","session":"makyx"}}]}""");
            check(m.Count == 1 && m[0].Connected && m[0].TmuxSocket == "makyx" && m[0].SshTarget == "benutzer@host-a", "Rechner mit tmux");
            var hello = MakyxJson.ParseHello("""{"schnittstelle":"v1","vault":"v","client":"Desktop","zeit":"2026-01-02T11:00:00Z"}""");
            check(hello.Version == "v1" && hello.Client == "Desktop", "Hallo-Antwort");
        });

        test("Makyx: Ereignisstrom-Parser setzt zerstückelte Blöcke richtig zusammen", () =>
        {
            string stream = ": keepalive\n\n" + Event("hallo", """{"schnittstelle":"v1"}""", 1) + "id: 2\r\nevent: lauf\r\ndata: {\"id\":\"E-1\"}\r\n\r\n" + "data: a\ndata: b\n\n";
            var all = new List<SseEvent>();
            var parser = new SseParser();
            foreach (char c in stream) all.AddRange(parser.Feed(c.ToString())); // Zeichen für Zeichen
            check(all.Count == 3 && all[0].Name == "hallo" && all[0].Id == "1" && all[1].Name == "lauf" && all[1].Data == "{\"id\":\"E-1\"}" && all[2].Name == "message" && all[2].Data == "a\nb", "drei Ereignisse, Kommentar ignoriert, Zeilenenden \\r\\n und \\n");
            var once = new SseParser().Feed(stream);
            check(once.Count == 3, "am Stück gleiches Ergebnis");
            check(new SseParser().Feed("event: x\n").Count == 0, "ohne Leerzeile kein Ereignis");
            bool tooLong = false;
            try { new SseParser().Feed(new string('x', SseParser.MaxLine + 5)); } catch (InvalidDataException) { tooLong = true; }
            check(tooLong, "überlange Zeile wird abgelehnt");
        });

        test("Makyx: Zustand meldet nur Neues, nicht den Bestand beim Start", () =>
        {
            var state = new MakyxState();
            var runs = MakyxJson.ParseRuns(RunsJson, out var time);
            var first = MakyxJson.ParseWaiting("""{"wartet":[{"id":"session:1","art":"rueckfrage","titel":"alt","link":"https://makyx.example/a"}]}""");
            check(state.LoadSnapshot(runs, first, time).NewWaiting.Count == 0 && state.WaitingCount == 1, "Bestand beim ersten Abruf bleibt still");
            var ev = state.Apply(new SseEvent("wartet", """{"id":"session:2","art":"entscheidung","titel":"neu","link":"https://makyx.example/b"}""", "5"));
            check(ev.NewWaiting.Count == 1 && ev.NewWaiting[0].Id == "session:2" && state.WaitingCount == 2, "neues Wartendes wird gemeldet");
            check(state.Apply(new SseEvent("wartet", """{"id":"session:2","art":"entscheidung","titel":"neu (geändert)","link":"https://makyx.example/b"}""", "6")).NewWaiting.Count == 0, "dasselbe Wartende nur einmal");
            state.Apply(new SseEvent("wartet_weg", """{"id":"session:2"}""", "7"));
            check(state.WaitingCount == 1, "wartet_weg entfernt");
            check(state.Apply(new SseEvent("wartet", """{"id":"session:2","art":"entscheidung","titel":"wieder","link":"https://makyx.example/b"}""", "8")).NewWaiting.Count == 1, "nach dem Wegfall zählt es wieder als neu");
            check(!state.Apply(new SseEvent("wartet", "{kaputt", null)).Changed && !state.Apply(new SseEvent("unbekannt", "{}", null)).Changed, "kaputtes oder unbekanntes Ereignis bricht nichts");
            state.Apply(new SseEvent("lauf", """{"id":"E-019","uuid":"u-19","status":"fertig","titel":"Im Terminal","projekt":"Beispiel","link":"https://makyx.example/a"}""", "9"));
            var order = state.Ordered().Select(r => r.Id).ToList();
            check(order[0] == "L-001" && order.Contains("E-019") && state.LastTime == time, "arbeitend vor beendet; Zeit der letzten Antwort gemerkt");
            var fresh = state.LoadSnapshot(runs, MakyxJson.ParseWaiting("""{"wartet":[{"id":"session:3","art":"rueckfrage","titel":"x","link":"https://makyx.example/c"}]}"""), time);
            check(fresh.NewWaiting.Count == 1 && state.WaitingCount == 1, "nach dem Nachholen: Neues wird gemeldet, Entfallenes verschwindet");
        });

        test("Makyx: Reihenfolge – wartet zuerst, dann arbeitet, dann zuletzt Beendetes, höchstens 30", () =>
        {
            var state = new MakyxState();
            var list = new List<MakyxRun>();
            for (int i = 0; i < 40; i++)
                list.Add(new($"E-{i}", $"u{i}", "einzelauftrag", "t", i == 5 ? MakyxStatus.Waiting : i == 7 ? MakyxStatus.Working : MakyxStatus.Done, "p", null, DateTimeOffset.UnixEpoch.AddMinutes(i), null, null, null, "", null, MakyxTerminal.None("x")));
            state.LoadSnapshot(list, [], null);
            var o = state.Ordered();
            check(o.Count == 30 && o[0].Id == "E-5" && o[1].Id == "E-7" && o[2].Id == "E-39" && o[3].Id == "E-38", "Sortierung und Obergrenze");
        });

        test("Makyx: CA-Pinnung akzeptiert nur die Kette der angegebenen CA", () =>
        {
            using var ca = MakeCa("CN=Test CA A");
            using var otherCa = MakeCa("CN=Test CA B");
            using var server = MakeServer(ca, "makyx.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            using var expired = MakeServer(ca, "makyx.example", DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow.AddDays(-10));
            string pin = MakyxTls.Fingerprint(ca), pinWithColons = string.Join(":", Enumerable.Range(0, 32).Select(i => pin.Substring(i * 2, 2).ToUpperInvariant()));
            using var chain = Chain(server, ca);
            const SslPolicyErrors chainErrors = SslPolicyErrors.RemoteCertificateChainErrors;
            check(MakyxTls.Validate(server, chain, chainErrors, pin), "richtiger Fingerprint");
            check(MakyxTls.Validate(server, chain, chainErrors, pinWithColons), "Fingerprint mit Doppelpunkten und Großbuchstaben");
            check(!MakyxTls.Validate(server, chain, chainErrors, MakyxTls.Fingerprint(otherCa)), "falscher Fingerprint");
            check(!MakyxTls.Validate(server, chain, chainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, pin), "falscher Servername bleibt ein Fehler");
            check(!MakyxTls.Validate(server, chain, chainErrors, "") && !MakyxTls.Validate(server, chain, chainErrors, "abc"), "fehlender oder ungültiger Fingerprint");
            check(!MakyxTls.Validate(null, chain, chainErrors, pin) && !MakyxTls.Validate(server, null, chainErrors, pin), "ohne Zertifikat oder Kette");
            using var expiredChain = Chain(expired, ca);
            check(!MakyxTls.Validate(expired, expiredChain, chainErrors, pin), "abgelaufenes Serverzertifikat bleibt ein Fehler");
            using var selfSigned = MakeCa("CN=makyx.example");
            using var selfChain = Chain(selfSigned, null);
            check(!MakyxTls.Validate(selfSigned, selfChain, chainErrors, MakyxTls.Fingerprint(selfSigned)), "ein selbstsigniertes Einzelzertifikat gilt nicht als CA-Kette");
        });

        test("Makyx: Einstellungen – Adresse, Fingerprint, Problem, kein Token in der Datei", () =>
        {
            var o = new MakyxOptions { Enabled = true, Address = " https://makyx.example:8801/ ", CaFingerprint = "AB:" + new string('c', 62) };
            check(o.TryBaseUri(out var u) && u!.ToString() == "https://makyx.example:8801/api/leitstand/v1/", "Basisadresse");
            check(o.Normalized().CaFingerprint == "ab" + new string('c', 62), "Fingerprint wird normalisiert");
            check(o.Problem() is null && new MakyxOptions { Address = "https://makyx.example" }.Problem() is { } p1 && p1.Contains("Fingerprint"), "https braucht den Fingerprint");
            check(new MakyxOptions { Address = "http://makyx.example:8800" }.Problem() is null, "http im LAN braucht keinen Fingerprint");
            foreach (string bad in new[] { "", "makyx.example", "ftp://makyx.example", "https://benutzer:pw@makyx.example", "https://makyx.example/?x=1", "https://makyx.example/#a" })
                check(!new MakyxOptions { Address = bad, CaFingerprint = new string('a', 64) }.TryBaseUri(out _), "ungültige Adresse: " + bad);
            string json = JsonSerializer.Serialize(new AppSettings { Makyx = o });
            check(!json.Contains("mkl_") && !json.Contains("oken", StringComparison.OrdinalIgnoreCase), "kein Token in den Einstellungen");
            var back = JsonSerializer.Deserialize<AppSettings>(json)!;
            check(back.Makyx.Address.Contains("makyx.example") && back.Makyx.Enabled && back.Makyx.Notify && back.Makyx.Collapsed, "Einstellungen überstehen die Speicherung; Vorgaben: Benachrichtigung an, eingeklappt");
            check(JsonSerializer.Deserialize<AppSettings>("{}")!.Makyx is { Enabled: false }, "ältere Einstellungsdatei ohne Makyx: aus");
        });

        test("Makyx: tmux-Anhängen nutzt eigenen Socket, Session und Fenster; böse Namen werden abgelehnt", () =>
        {
            var profile = new Profile { Name = "rechner-a", Destination = "rechner-a" };
            var args = SshCommand.MakyxTmuxAttachArguments(profile, "makyx", "makyx", "auftrag-19");
            check(args.Contains("-tt") && args[^2] == "rechner-a" && args[^1] == "tmux -L 'makyx' attach-session -t 'makyx:auftrag-19'", "Befehl: " + args[^1]);
            foreach (var (s, se, w) in new[] { ("ma kyx", "makyx", "a"), ("makyx", "ma'kyx", "a"), ("makyx", "makyx", "a;rm -rf /"), ("makyx", "makyx", ""), ("makyx", "makyx", new string('a', 41)), ("makyx", "$(id)", "a") })
            {
                bool refused = false;
                try { SshCommand.MakyxTmuxAttachArguments(profile, s, se, w); } catch (ArgumentException) { refused = true; }
                check(refused, $"abgelehnt: {s}/{se}/{w}");
            }
        });

        test("Makyx: Fenster auf dem eigenen Socket werden abgefragt, ohne Ansichts-Doppel, und dem Server zugeordnet", () =>
        {
            var profile = new Profile { Id = "p1", Name = "rechner-a", Destination = "benutzer@rechner-a" };
            check(SshCommand.TmuxWindowsArguments(profile, "makyx")[^1].StartsWith("tmux -L 'makyx' list-windows -a -F "), "Abfrage mit Socket");
            check(SshCommand.TmuxWindowsArguments(profile)[^1].StartsWith("tmux list-windows -a -F "), "ohne Socket wie bisher");
            check(SshCommand.TmuxAttachArguments(profile, "$1", "@3", "makyx")[^1] == "tmux -L 'makyx' attach-session -t '$1:@3'", "Anhängen mit Socket");
            string script = WindowStripParser.Script("$1", 1, "makyx");
            check(script.Contains("tmux -L 'makyx' list-windows -t") && script.Contains("tmux -L 'makyx' capture-pane") && !script.Contains("; tmux list-windows"), "Fensterleiste fragt denselben Socket: " + script);
            bool refused = false;
            try { SshCommand.TmuxWindowsArguments(profile, "ma'kyx"); } catch (ArgumentException) { refused = true; }
            check(refused, "ungültiger Socket wird abgelehnt");

            string output = string.Join("\n",
                "$0\t@0\t0\t1\tmakyx\tuebersicht\tsleep",
                "$0\t@1\t1\t0\tmakyx\tlauf9-bau\tclaude",
                "$0\t@2\t2\t0\tmakyx\tsession-frei\tclaude",
                "$3\t@1\t1\t1\tansicht-f9363\tlauf9-bau\tclaude",
                "$4\t@2\t2\t1\tansicht-0aa12\tsession-frei\tclaude");
            var sessions = MakyxTmux.Sessions(TmuxWindowParser.Parse(output));
            check(sessions.Count == 1 && sessions[0].Name == "Makyx · makyx" && sessions[0].Id == "$0", "eine Session, Ansichten fallen weg: " + string.Join(",", sessions.Select(s => s.Name)));
            check(sessions[0].Windows.Select(w => w.Name).SequenceEqual(["lauf9-bau", "session-frei"]), "zwei Fenster, Platzhalter ohne Programm fehlt");

            var other = new Profile { Id = "p2", Name = "rechner-b", Destination = "rechner-b" };
            var machines = new[] {
                new MakyxMachine("m1", "makyx-rechner-a", "verbunden", true, "1.0", "benutzer@rechner-a", "makyx"),
                new MakyxMachine("m2", "makyx-rechner-c", "verbunden", true, "1.0", "rechner-c", "makyx"),
                new MakyxMachine("m3", "rechner-b", "verbunden", true, "1.0", "rechner-b", null) };
            check(MakyxTmux.SocketsFor(profile, [profile, other], machines).SequenceEqual(["makyx"]), "Rechner über das SSH-Ziel zugeordnet");
            check(MakyxTmux.SocketsFor(other, [profile, other], machines).Count == 0, "Rechner ohne tmux-Socket liefert nichts");
        });

        test("Makyx: Profil des Rechners wird zugeordnet (Zuordnung, Name, Ziel)", () =>
        {
            var a = new Profile { Id = "p1", Name = "Arbeitsrechner", Destination = "benutzer@host-a" };
            var b = new Profile { Id = "p2", Name = "rechner-b", Destination = "rechner-b" };
            var c = new Profile { Id = "p3", Name = "Sonstiger", Destination = "host-c" };
            var all = new[] { a, b, c };
            check(MakyxProfileMatcher.Match(all, "rechner-x", "benutzer@host-a", null)?.Id == "p1", "über das SSH-Ziel");
            check(MakyxProfileMatcher.Match(all, "RECHNER-B", null, null)?.Id == "p2", "über den Namen, ohne Groß-/Kleinschreibung");
            check(MakyxProfileMatcher.Match(all, "rechner-x", "anderer@host-c", null)?.Id == "p3", "über den Hostnamen");
            check(MakyxProfileMatcher.Match(all, "rechner-b", null, new Dictionary<string, string> { ["rechner-b"] = "p3" })?.Id == "p3", "ausdrückliche Zuordnung hat Vorrang");
            check(MakyxProfileMatcher.Match(all, "rechner-b", null, new Dictionary<string, string> { ["rechner-b"] = "gibt-es-nicht" })?.Id == "p2", "veraltete Zuordnung fällt auf automatisch zurück");
            check(MakyxProfileMatcher.Match(all, "unbekannt", "x@y", null) is null && MakyxProfileMatcher.Match(all, null, null, null) is null, "ohne Treffer null");
        });

        test("Makyx: Befehlspalette zeigt laufende Aufträge, Terminal-Eintrag nur mit tmux-Fenster", () =>
        {
            var runs = MakyxJson.ParseRuns(RunsJson, out _);
            var items = PaletteBuilder.Build(new AppSettings(), [], [], [], [], runs);
            var makyx = items.Where(i => i.Group == PaletteBuilder.MakyxGroup).ToList();
            check(makyx.Count == 3 && makyx.Any(i => i.Id == "makyx:u-19") && makyx.Any(i => i.Id == "makyxterm:u-19") && makyx.Any(i => i.Id == "makyx:u-l1") && !makyx.Any(i => i.Id == "makyx:u-18"), "nur aktive Aufträge; Terminal-Eintrag nur bei u-19");
            check(makyx.First(i => i.Id == "makyx:u-19").Subtitle.Contains("wartet") && PaletteBuilder.GroupOrder.Contains(PaletteBuilder.MakyxGroup), "Untertitel mit Status; Gruppe in der Reihenfolge");
        });
    }

    private static X509Certificate2 MakeCa(string subject)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 MakeServer(X509Certificate2 ca, string host, DateTimeOffset from, DateTimeOffset to)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        using var signed = req.Create(ca, from, to, serial);
        return new X509Certificate2(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pfx));
    }

    private static X509Chain Chain(X509Certificate2 leaf, X509Certificate2? ca)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (ca is not null) chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(ca.Export(X509ContentType.Cert)));
        chain.Build(leaf);
        return chain;
    }
}
