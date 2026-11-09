using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TerminalMgr.Core;

/// <summary>Trust for the Makyx runner channel: its certificate chain ends in an own CA that Windows does not know. The CA is pinned by its SHA-256 fingerprint
/// (shown in the Makyx dialog); the server name still has to match.</summary>
public static class MakyxTls
{
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    public static bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors, string pinnedFingerprint)
    {
        string? pin = MakyxOptions.NormalizeFingerprint(pinnedFingerprint);
        if (pin is null || certificate is null || chain is null) return false;
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        if (chain.ChainElements.Count < 2) return false;
        var root = chain.ChainElements[^1].Certificate;
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Fingerprint(root)), System.Text.Encoding.ASCII.GetBytes(pin))) return false;
        // The only accepted problem is "the root is not trusted by Windows" (that is what the pin replaces); expired, revoked or broken signatures stay errors.
        foreach (var element in chain.ChainElements)
            foreach (var status in element.ChainElementStatus)
                if (status.Status is not (X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.NoError or X509ChainStatusFlags.PartialChain)) return false;
        return true;
    }
}
