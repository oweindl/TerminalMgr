using System.Security.Cryptography;

namespace TerminalMgr.Core;

/// <summary>
/// Rules of the Makyx window in TerminalMgr: it shows only pages of the Makyx interface (the origins of the links it was asked to open);
/// everything else goes to the default browser. A server certificate from the own Makyx CA is accepted only when the CA matches the
/// fingerprint of the Makyx settings.
/// </summary>
public static class MakyxBrowserRules
{
    /// <summary>"scheme://host:port" of an http or https address, or null.</summary>
    public static string? Origin(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || u.UserInfo.Length > 0) return null;
        return $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}";
    }

    /// <summary>May the Makyx window show this address itself?</summary>
    public static bool IsAllowed(string? url, IReadOnlyCollection<string> origins) => Origin(url) is { } origin && origins.Contains(origin);

    /// <summary>A one-time sign-in code from Makyx: short, only URL-safe characters (it goes into the address of /anmelden).</summary>
    public static bool IsLoginCode(string? code) => code is { Length: >= 8 and <= 200 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');

    /// <summary>
    /// The sign-in address for a link (note Makyx-PoC/13 E): "&lt;origin&gt;/anmelden?code=…&amp;ziel=&lt;path&gt;". The target is only the relative path of the
    /// link (Makyx also accepts nothing else), so the sign-in never leads away from Makyx. Null for an invalid link or code.
    /// </summary>
    public static string? LoginUrl(string? link, string? code)
    {
        if (Origin(link) is not { } origin || !IsLoginCode(code)) return null;
        var u = new Uri(link!);
        string target = u.PathAndQuery + u.Fragment;
        if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal)) target = "/";
        return origin + "/anmelden?code=" + Uri.EscapeDataString(code!) + "&ziel=" + Uri.EscapeDataString(target);
    }

    /// <summary>Is this the sign-in address (its code is not shown in the address line)?</summary>
    public static bool IsLoginAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath == "/anmelden";

    /// <summary>Does the certificate or one of its issuers (DER) have the pinned SHA-256 fingerprint?</summary>
    public static bool ChainMatches(IEnumerable<byte[]> certificates, string? pinnedFingerprint)
    {
        string? pin = MakyxOptions.NormalizeFingerprint(pinnedFingerprint);
        if (pin is null) return false;
        return certificates.Any(der => Convert.ToHexString(SHA256.HashData(der)).Equals(pin, StringComparison.OrdinalIgnoreCase));
    }
}
