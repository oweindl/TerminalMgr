using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

/// <summary>Settings of the Makyx dashboard. The token is not part of it: it lives in the Windows Credential Manager (see MakyxCredentialStore).</summary>
public sealed record MakyxOptions
{
    public bool Enabled { get; init; }
    /// <summary>Address of the Makyx runner channel (TLS, own CA), for example https://makyx.example:8801.</summary>
    public string Address { get; init; } = "";
    /// <summary>SHA-256 of the CA certificate as Makyx shows it (hex, colons allowed).</summary>
    public string CaFingerprint { get; init; } = "";
    /// <summary>Toast when something new waits for the user.</summary>
    public bool Notify { get; init; } = true;
    public bool Collapsed { get; init; } = true;
    public double Height { get; init; } = 200;
    /// <summary>Makyx machine name to profile id; a machine without an entry is matched automatically (see <see cref="MakyxProfileMatcher"/>).</summary>
    public Dictionary<string, string> MachineProfiles { get; init; } = [];

    public MakyxOptions Normalized() => this with
    {
        Address = Address.Trim(), CaFingerprint = NormalizeFingerprint(CaFingerprint) ?? CaFingerprint.Trim(), Height = Math.Clamp(Height, 80, 600),
        MachineProfiles = MachineProfiles.Where(p => p.Key.Length is > 0 and <= 100 && p.Value.Length is > 0 and <= 64).ToDictionary(p => p.Key, p => p.Value)
    };

    /// <summary>The fingerprint as 64 lower-case hex characters, or null when it is not one.</summary>
    public static string? NormalizeFingerprint(string? text)
    {
        if (text is null) return null;
        string hex = text.Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();
        return Regex.IsMatch(hex, "^[0-9a-f]{64}$") ? hex : null;
    }

    /// <summary>Base address of the API (…/api/leitstand/v1/). Only http and https, without user info, query or fragment.</summary>
    public bool TryBaseUri(out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || string.IsNullOrEmpty(u.Host)) return false;
        uri = new Uri($"{u.Scheme}://{u.Authority}/api/leitstand/v1/");
        return true;
    }

    public bool UsesTls => Address.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the settings are not usable yet, or null.</summary>
    public string? Problem()
    {
        if (!TryBaseUri(out _)) return "Adresse fehlt oder ist ungültig (https://host:8801).";
        if (UsesTls && NormalizeFingerprint(CaFingerprint) is null) return "CA-Fingerprint fehlt oder ist ungültig (64 Hex-Zeichen, wie im Makyx-Dialog).";
        return null;
    }
}

/// <summary>Finds the TerminalMgr profile that belongs to a Makyx machine.</summary>
public static class MakyxProfileMatcher
{
    public static Profile? Match(IReadOnlyList<Profile> profiles, string? machine, string? sshTarget, IReadOnlyDictionary<string, string>? explicitMap = null)
    {
        if (machine is not null && explicitMap is not null && explicitMap.TryGetValue(machine, out string? id) && profiles.FirstOrDefault(p => p.Id == id) is { } chosen) return chosen;
        if (machine is not null && profiles.FirstOrDefault(p => string.Equals(p.Name, machine, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        if (!string.IsNullOrWhiteSpace(sshTarget))
        {
            string host = sshTarget.Contains('@') ? sshTarget[(sshTarget.LastIndexOf('@') + 1)..] : sshTarget;
            if (profiles.FirstOrDefault(p => string.Equals(p.Destination, sshTarget, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
            if (profiles.FirstOrDefault(p => string.Equals(p.Destination, host, StringComparison.OrdinalIgnoreCase)
                || p.Destination.EndsWith("@" + host, StringComparison.OrdinalIgnoreCase)) is { } byHost) return byHost;
        }
        return null;
    }
}
