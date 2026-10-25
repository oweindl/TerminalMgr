namespace TerminalMgr.Core;

public sealed record ProfileProblem(string ProfileName, string Message);

/// <summary>Reads the output of "ssh -G alias" (the effective settings, no connection is made) and finds what keeps a profile from connecting.</summary>
public static class ConfigCheck
{
    private static readonly string[] DefaultKeyNames = ["id_rsa", "id_ecdsa", "id_ecdsa_sk", "id_ed25519", "id_ed25519_sk", "id_xmss", "id_dsa"];

    public static Dictionary<string, List<string>> ParseEffective(string output)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            string key = line[..space].ToLowerInvariant();
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(line[(space + 1)..].Trim());
        }
        return values;
    }

    /// <param name="fileExists">Tests replace the file system.</param>
    public static IReadOnlyList<ProfileProblem> Evaluate(Profile profile, string sshGOutput, Func<string, bool> fileExists, string home)
    {
        var problems = new List<ProfileProblem>();
        var effective = ParseEffective(sshGOutput);
        string Expand(string path) => path.StartsWith("~/") || path.StartsWith("~\\") ? Path.Combine(home, path[2..]) : path;

        var keys = effective.GetValueOrDefault("identityfile") ?? [];
        var explicitKeys = keys.Where(k => !DefaultKeyNames.Contains(Path.GetFileName(k), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (string key in explicitKeys.Where(k => !fileExists(Expand(k))))
            problems.Add(new(profile.Name, $"Schlüsseldatei fehlt: {key} (separat auf diesen Rechner kopieren)"));
        if (explicitKeys.Count == 0 && !keys.Any(k => fileExists(Expand(k))))
            problems.Add(new(profile.Name, "Im Ordner .ssh liegt kein Standard-Schlüssel; die Anmeldung klappt nur mit einem SSH-Agenten oder einem Passwort."));

        bool direct = profile.Destination.Contains('@') || profile.Destination.Contains('.');
        string? host = effective.GetValueOrDefault("hostname")?.FirstOrDefault();
        if (!direct && host is not null && string.Equals(host, profile.Destination, StringComparison.OrdinalIgnoreCase))
            problems.Add(new(profile.Name, $"Der Host „{profile.Destination}“ ist in der SSH-Config nicht definiert; der Name wird direkt aufgelöst."));
        return problems;
    }
}
