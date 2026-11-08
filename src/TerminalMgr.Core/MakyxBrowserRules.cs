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

    /// <summary>Does the certificate or one of its issuers (DER) have the pinned SHA-256 fingerprint?</summary>
    public static bool ChainMatches(IEnumerable<byte[]> certificates, string? pinnedFingerprint)
    {
        string? pin = MakyxOptions.NormalizeFingerprint(pinnedFingerprint);
        if (pin is null) return false;
        return certificates.Any(der => Convert.ToHexString(SHA256.HashData(der)).Equals(pin, StringComparison.OrdinalIgnoreCase));
    }
}
