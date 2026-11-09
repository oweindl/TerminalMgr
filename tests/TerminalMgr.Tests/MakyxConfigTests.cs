using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TerminalMgr.Core;

/// <summary>The Makyx configuration text (note 14): recognizing, decoding, version, certificate against fingerprint, expiry, masking. Only neutral names (makyx.example, rechner-a).</summary>
internal static class MakyxConfigTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-02T12:00:00Z");

    private static (string Pem, string Fingerprint) MakeCa(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (cert.ExportCertificatePem(), Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant());
    }

    /// <summary>A configuration text as JSON; every part can be replaced to build wrong ones.</summary>
    public static string Json(string fingerprint, string? pem, string kopplung = "\"kopplung\":{\"code\":\"einmalcode-0001\",\"gueltig_bis\":\"2026-01-02T12:10:00Z\"}", string version = "1", string api = "https://makyx.example:8801/api/leitstand/v1/", string zugang = "\"zugang\":{\"id\":\"z1\",\"name\":\"TerminalMgr Desktop\"}") =>
        "{\"format\":\"makyx-leitstand\",\"version\":" + version + ",\"erstellt\":\"2026-01-02T12:00:00Z\","
        + "\"makyx\":{\"name\":\"Makyx Beispiel\",\"oberflaeche\":\"https://makyx.example:8800\",\"api\":\"" + api + "\"},"
        + "\"ca\":{\"fingerprint_sha256\":\"" + string.Join(':', Enumerable.Range(0, 32).Select(i => fingerprint.Substring(i * 2, 2).ToUpperInvariant())) + "\"" + (pem is null ? "" : ",\"pem\":" + System.Text.Json.JsonSerializer.Serialize(pem)) + "},"
        + zugang + (kopplung.Length > 0 ? "," + kopplung : "") + "}";

    public static (string Pem, string Fingerprint) NewCa() => MakeCa("CN=Makyx Beispiel CA");

    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Makyx-Konfiguration: Text mit Präfix und reines JSON werden gelesen, Vorschau ohne Code", () =>
        {
            var (pem, fingerprint) = NewCa();
            string json = Json(fingerprint, pem);
            var result = MakyxConfigText.Parse(MakyxConfigText.Encode(json), Now);
            check(result.Ok && result.Error is null, "Text mit Präfix: " + result.Error);
            var c = result.Config!;
            check(c.Address == "https://makyx.example:8801" && c.ApiAddress == "https://makyx.example:8801/api/leitstand/v1/" && c.UiAddress == "https://makyx.example:8800", "Adressen: " + c.Address);
            check(c.CaFingerprint == fingerprint && c.AccessName == "TerminalMgr Desktop" && c.MakyxName == "Makyx Beispiel" && c.Code == "einmalcode-0001" && c.Token is null && !c.HasToken, "Felder");
            check(c.CodeValidUntil == DateTimeOffset.Parse("2026-01-02T12:10:00Z"), "gültig bis");
            string preview = string.Join("\n", MakyxConfigText.Preview(c));
            check(preview.Contains("Makyx Beispiel") && preview.Contains(MakyxConfigText.Group(fingerprint)) && preview.Contains("Einmal-Code") && preview.Contains("TerminalMgr Desktop") && !preview.Contains("einmalcode"), "Vorschau zeigt Name, Fingerprint, Art, aber nie den Code");
            check(MakyxConfigText.Parse(json, Now).Ok, "reines JSON");
            string lines = MakyxConfigText.Encode(json).Insert(40, "\r\n  ").Insert(100, "\n");
            check(MakyxConfigText.Parse(lines, Now).Ok, "Zeilenumbrüche und Leerzeichen im Text stören nicht");
            check(MakyxConfigText.Parse(Json(fingerprint, null), Now).Ok, "ohne PEM genügt der Fingerprint");
        });

        test("Makyx-Konfiguration: Token im Text (Variante B) wird erkannt, Form wird geprüft", () =>
        {
            var (_, fingerprint) = NewCa();
            string withToken = Json(fingerprint, null, kopplung: "", zugang: "\"zugang\":{\"id\":\"z1\",\"name\":\"Zugang\",\"token\":\"mkl_beispieltoken_0001\"}");
            var result = MakyxConfigText.Parse(MakyxConfigText.Encode(withToken), Now);
            check(result.Ok && result.Config!.HasToken && result.Config.Code is null, "Token wird übernommen: " + result.Error);
            string preview = string.Join("\n", MakyxConfigText.Preview(result.Config!));
            check(preview.Contains("Token im Text") && !preview.Contains("mkl_"), "die Vorschau nennt die Art, aber nie das Token");
            string badToken = Json(fingerprint, null, kopplung: "", zugang: "\"zugang\":{\"name\":\"Z\",\"token\":\"geheim\"}");
            check(MakyxConfigText.Parse(badToken, Now) is { Ok: false, Error: { } e } && !e.Contains("geheim"), "ein Token ohne mkl_ wird abgelehnt, ohne es zu nennen");
        });

        test("Makyx-Konfiguration: verständliche Fehler (leer, fremd, unvollständig, neuere Version, abgelaufen)", () =>
        {
            var (pem, fingerprint) = NewCa();
            string good = MakyxConfigText.Encode(Json(fingerprint, pem));
            string? Error(string? text) => MakyxConfigText.Parse(text, Now).Error;
            check(Error(null) is { } a && a.Contains("kein Text") && Error("   ") is not null, "leer");
            check(Error("hallo welt") is { } b && b.Contains("keine Makyx-Konfiguration"), "fremder Text");
            check(Error("makyx-leitstand:") is { } c1 && c1.Contains("unvollständig") && Error("makyx-leitstand:1:") is { } c2 && c2.Contains("unvollständig"), "Präfix ohne Inhalt");
            check(Error(good[..(good.Length / 2)]) is { } d && (d.Contains("unvollständig") || d.Contains("beschädigt")), "abgeschnittener Text: " + Error(good[..(good.Length / 2)]));
            check(Error("makyx-leitstand:2:" + good[(good.IndexOf(":1:", StringComparison.Ordinal) + 3)..]) is { } e && e.Contains("neuer") && e.Contains("aktualisieren"), "Version 2 im Präfix");
            check(Error(Json(fingerprint, pem, version: "7")) is { } f && f.Contains("neuer"), "Version 7 im JSON");
            check(Error("{\"format\":\"etwas-anderes\",\"version\":1}") is { } g && g.Contains("Format"), "anderes Format");
            check(Error(Json(fingerprint, pem, kopplung: "\"kopplung\":{\"code\":\"einmalcode-0001\",\"gueltig_bis\":\"2026-01-02T11:00:00Z\"}")) is { } h && h.Contains("abgelaufen") && h.Contains("neu erzeugen") && !h.Contains("einmalcode"), "abgelaufener Code");
            check(Error(Json(fingerprint, pem, kopplung: "")) is { } i && i.Contains("unvollständig"), "weder Code noch Token");
            check(Error(Json(fingerprint, pem, kopplung: "\"kopplung\":{\"code\":\"x y\"}")) is { } j && j.Contains("Einmal-Code"), "Code in falscher Form");
            check(Error(Json(fingerprint, pem, api: "ftp://makyx.example/")) is { } k && k.Contains("Adresse"), "fremdes Schema");
            check(Error(Json(fingerprint, pem, api: "https://benutzer:geheim@makyx.example/")) is { } l && l.Contains("Adresse") && !l.Contains("geheim"), "Benutzerangaben in der Adresse");
            check(Error(new string('a', MakyxConfigText.MaxLength + 1)) is { } m && m.Contains("zu lang"), "zu lang");
            check(MakyxConfigText.Parse(good, Now).Ok, "der gute Text bleibt gut");
        });

        test("Makyx-Konfiguration: das CA-Zertifikat muss zum Fingerprint passen", () =>
        {
            var (pemA, fingerprintA) = NewCa();
            var (pemB, _) = MakeCa("CN=Fremde CA");
            check(MakyxConfigText.PemMatches(pemA, fingerprintA) && !MakyxConfigText.PemMatches(pemB, fingerprintA) && !MakyxConfigText.PemMatches("kein pem", fingerprintA), "PemMatches");
            var wrong = MakyxConfigText.Parse(Json(fingerprintA, pemB), Now);
            check(!wrong.Ok && wrong.Error!.Contains("passt nicht zum Fingerprint") && wrong.Error.Contains("nichts übernommen"), "fremde CA im Text: Abbruch");
            check(MakyxConfigText.Parse(Json(fingerprintA, pemA), Now).Ok, "passende CA");
            string badFingerprint = Json(fingerprintA, null).Replace(MakyxConfigText.Group(fingerprintA), "ZZ:ZZ");
            check(MakyxConfigText.Parse(badFingerprint, Now) is { Ok: false, Error: { } bad } && bad.Contains("Fingerprint"), "ungültiger Fingerprint");
        });

        test("Makyx-Konfiguration: der Clipboard-Verlauf erkennt den Text und zeigt ihn nur unter seinem Namen", () =>
        {
            var (_, fingerprint) = NewCa();
            string text = MakyxConfigText.Encode(Json(fingerprint, null));
            check(MakyxConfigText.LooksLikeConfig(text) && MakyxConfigText.LooksLikeConfig("  " + Json(fingerprint, null)) && MakyxConfigText.LooksLikeConfig("makyx-leitstand:1:abc"), "erkannt");
            check(!MakyxConfigText.LooksLikeConfig("git status") && !MakyxConfigText.LooksLikeConfig("{\"a\":1}") && !MakyxConfigText.LooksLikeConfig(null) && !MakyxConfigText.LooksLikeConfig(""), "gewöhnlicher Text nicht");
            var items = PaletteBuilder.Build(new AppSettings(), [], [], [text, "ls -la"], []);
            var clips = items.Where(i => i.Group == PaletteBuilder.ClipboardGroup).ToList();
            check(clips.Count == 2 && clips[0].Title == MakyxConfigText.MaskedLabel && clips[1].Title == "ls -la" && !clips.Any(c => c.Title.Contains("makyx-leitstand:") || c.Subtitle.Contains("makyx-leitstand:")), "die Befehlspalette zeigt nur den Namen");
            check(MakyxConfigText.MaskedLabel == "Makyx-Leitstand-Konfiguration", "Anzeigename");
        });
    }
}
