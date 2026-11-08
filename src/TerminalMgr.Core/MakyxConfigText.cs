using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

/// <summary>What the configuration text of Makyx says (see notes 13 and 14): where the runner channel is, which CA to trust, and either a one-time code or a token.</summary>
public sealed record MakyxConfig(
    string MakyxName, string UiAddress, string ApiAddress, string CaFingerprint, string? CaPem, string AccessName,
    string? Code, DateTimeOffset? CodeValidUntil, string? Token, DateTimeOffset? Created)
{
    public bool HasToken => Token is not null;
    /// <summary>The address as the settings keep it: scheme, host and port of the API.</summary>
    public string Address => new Uri(ApiAddress).GetLeftPart(UriPartial.Authority);
}

public sealed record MakyxConfigResult(MakyxConfig? Config, string? Error)
{
    public bool Ok => Config is not null;
}

/// <summary>
/// Reads the text that Makyx shows for a new dashboard access: <c>makyx-leitstand:1:&lt;base64url(JSON)&gt;</c> in one line, or the plain JSON.
/// Nothing is stored here; the caller decides. A token or code is never part of an error message.
/// </summary>
public static class MakyxConfigText
{
    public const string Prefix = "makyx-leitstand:";
    public const string FormatName = "makyx-leitstand";
    public const int SupportedVersion = 1;
    public const int MaxLength = 100_000;
    private static readonly Regex CodePattern = new("^[A-Za-z0-9._~-]{8,200}$", RegexOptions.Compiled);

    /// <summary>Does this text look like a Makyx configuration (for the clipboard list, which must not show it)?</summary>
    public static bool LooksLikeConfig(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return false;
        string t = text.TrimStart();
        if (t.StartsWith(Prefix, StringComparison.Ordinal)) return true;
        return t.StartsWith('{') && t.Contains("\"makyx-leitstand\"", StringComparison.Ordinal) && t.Contains("\"format\"", StringComparison.Ordinal);
    }

    /// <summary>The shortened, fixed text shown in the clipboard list instead of the configuration.</summary>
    public const string MaskedLabel = "Makyx-Leitstand-Konfiguration";

    public static MakyxConfigResult Parse(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail("Es ist kein Text eingefügt.");
        string t = text.Trim();
        if (t.Length > MaxLength) return Fail("Der Text ist zu lang für eine Makyx-Konfiguration.");
        string json;
        if (t.StartsWith(Prefix, StringComparison.Ordinal))
        {
            string rest = t[Prefix.Length..];
            int colon = rest.IndexOf(':');
            if (colon <= 0) return Fail("Der Text ist unvollständig (nach „makyx-leitstand:“ fehlen Version und Inhalt).");
            if (!int.TryParse(rest[..colon], out int version)) return Fail("Das Format ist unbekannt (die Version fehlt).");
            if (version > SupportedVersion) return Fail($"Diese Konfiguration ist neuer (Version {version}). Bitte TerminalMgr aktualisieren.");
            if (version < 1) return Fail("Das Format ist unbekannt.");
            string payload = string.Concat(rest[(colon + 1)..].Where(c => !char.IsWhiteSpace(c)));
            if (payload.Length == 0) return Fail("Der Text ist unvollständig (der Inhalt fehlt).");
            try { json = Encoding.UTF8.GetString(DecodeBase64Url(payload)); }
            catch (FormatException) { return Fail("Der Text ist unvollständig oder beschädigt (er lässt sich nicht lesen). Bitte noch einmal vollständig kopieren."); }
        }
        else if (t.StartsWith('{')) json = t;
        else return Fail("Das ist keine Makyx-Konfiguration (sie beginnt mit „makyx-leitstand:“).");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { return Fail("Der Text ist unvollständig oder beschädigt (kein gültiges JSON). Bitte noch einmal vollständig kopieren."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("Das Format ist unbekannt.");
            if (Text(root, "format") != FormatName) return Fail("Das ist keine Makyx-Leitstand-Konfiguration (Format unbekannt).");
            if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int jsonVersion))
                return Fail("Das Format ist unbekannt (die Version fehlt).");
            if (jsonVersion > SupportedVersion) return Fail($"Diese Konfiguration ist neuer (Version {jsonVersion}). Bitte TerminalMgr aktualisieren.");
            if (jsonVersion < 1) return Fail("Das Format ist unbekannt.");

            var makyx = Obj(root, "makyx"); var ca = Obj(root, "ca"); var access = Obj(root, "zugang"); var pairing = Obj(root, "kopplung");
            if (makyx is null || ca is null || access is null) return Fail("Der Text ist unvollständig (Angaben zu Makyx, CA oder Zugang fehlen).");

            string api = Text(makyx.Value, "api") ?? "";
            if (!Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrEmpty(apiUri.Host) || apiUri.UserInfo.Length > 0 || apiUri.Query.Length > 0 || apiUri.Fragment.Length > 0)
                return Fail("Die Adresse der Schnittstelle in der Konfiguration fehlt oder ist ungültig.");
            string ui = Text(makyx.Value, "oberflaeche") ?? "";
            if (ui.Length > 0 && (!Uri.TryCreate(ui, UriKind.Absolute, out var uiUri) || (uiUri.Scheme != Uri.UriSchemeHttps && uiUri.Scheme != Uri.UriSchemeHttp)))
                return Fail("Die Adresse der Makyx-Oberfläche in der Konfiguration ist ungültig.");

            string? fingerprint = MakyxOptions.NormalizeFingerprint(Text(ca.Value, "fingerprint_sha256"));
            if (fingerprint is null) return Fail("Der CA-Fingerprint in der Konfiguration fehlt oder ist ungültig.");
            string? pem = Text(ca.Value, "pem");
            if (!string.IsNullOrWhiteSpace(pem) && !PemMatches(pem, fingerprint))
                return Fail("Das CA-Zertifikat im Text passt nicht zum Fingerprint. Es wird nichts übernommen; bitte die Konfiguration in Makyx neu erzeugen.");

            string? code = pairing is null ? null : Text(pairing.Value, "code");
            string? token = Text(access.Value, "token") ?? Text(root, "token");
            DateTimeOffset? validUntil = pairing is null ? null : Time(pairing.Value, "gueltig_bis");
            if (token is not null && (!token.StartsWith("mkl_", StringComparison.Ordinal) || token.Length > 100 || token.Any(char.IsWhiteSpace)))
                return Fail("Das Token in der Konfiguration hat nicht die erwartete Form.");
            if (token is null && code is null) return Fail("Der Text ist unvollständig (weder Einmal-Code noch Token).");
            if (token is null && !CodePattern.IsMatch(code!)) return Fail("Der Einmal-Code in der Konfiguration hat nicht die erwartete Form.");
            if (token is null && validUntil is { } until && until <= now)
                return Fail("Der Code ist abgelaufen. In Makyx unter Leitstand-Zugänge die Konfiguration neu erzeugen.");

            var config = new MakyxConfig(
                Limit(Text(makyx.Value, "name"), 100) ?? "Makyx", ui, apiUri.AbsoluteUri, fingerprint, string.IsNullOrWhiteSpace(pem) ? null : pem,
                Limit(Text(access.Value, "name"), 100) ?? "Zugang", token is null ? code : null, token is null ? validUntil : null, token, Time(root, "erstellt"));
            return new MakyxConfigResult(config, null);
        }
    }

    /// <summary>The lines of the preview before "Übernehmen". The token or code itself is never shown.</summary>
    public static IReadOnlyList<string> Preview(MakyxConfig config) =>
    [
        "Makyx: " + config.MakyxName,
        "Oberfläche: " + (config.UiAddress.Length > 0 ? config.UiAddress : "–"),
        "Schnittstelle: " + config.ApiAddress,
        "Zugang: " + config.AccessName,
        "CA-Fingerprint (mit Makyx vergleichen): " + Group(config.CaFingerprint),
        "Art: " + (config.HasToken ? "Token im Text (geheim, danach die Zwischenablage leeren)" : "Einmal-Code" + (config.CodeValidUntil is { } u ? ", gültig bis " + u.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : ""))
    ];

    /// <summary>The fingerprint as it is shown in Makyx: upper case, in pairs with colons.</summary>
    public static string Group(string fingerprint) => string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2).ToUpperInvariant()));

    /// <summary>The certificate in the PEM text has exactly this SHA-256 fingerprint.</summary>
    public static bool PemMatches(string pem, string fingerprint)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(fingerprint));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    private static MakyxConfigResult Fail(string message) => new(null, message);

    private static JsonElement? Obj(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? Limit(string? text, int length) => text is null ? null : new string(text.Where(c => !char.IsControl(c)).ToArray()) is { Length: > 0 } clean ? clean[..Math.Min(clean.Length, length)] : null;

    private static DateTimeOffset? Time(JsonElement parent, string name) =>
        Text(parent, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static byte[] DecodeBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 1 => throw new FormatException(), _ => "" };
        return Convert.FromBase64String(s);
    }

    /// <summary>For tests and for a Makyx stand-in: the text the way Makyx builds it.</summary>
    public static string Encode(string json) => Prefix + SupportedVersion + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
