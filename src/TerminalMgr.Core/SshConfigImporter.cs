using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

// Discover explicit aliases only. OpenSSH itself resolves Host/Match precedence and options.
public static partial class SshConfigImporter
{
    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'|([^\\s]+)")]
    private static partial Regex Tokens();

    public static IReadOnlyList<Profile> Import(string path)
    {
        path = Path.GetFullPath(path);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadAliases(path, aliases, visited, 0);
        return aliases.Select(alias => new Profile { Name = alias, Destination = alias, ConfigPath = path }).ToList();
    }

    private static void ReadAliases(string path, HashSet<string> aliases, HashSet<string> visited, int depth)
    {
        if (depth > 16 || !visited.Add(path)) return;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOfAny([' ', '\t', '=']);
            if (split < 0) continue;
            string key = line[..split];
            string value = line[(split + 1)..].TrimStart(' ', '\t', '=');
            var tokens = Tokens().Matches(value).Select(m => m.Groups[1].Success ? m.Groups[1].Value :
                m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).TakeWhile(t => !t.StartsWith('#'));
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string alias in tokens)
                    if (!alias.Any(c => "*?!".Contains(c)) && !alias.StartsWith('-')) aliases.Add(alias);
            }
            else if (key.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string include in tokens)
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string expanded = include.StartsWith("~/") ? Path.Combine(home, include[2..]) : include;
                    // OpenSSH client resolves relative Include paths under ~/.ssh.
                    if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(home, ".ssh", expanded);
                    var directory = Path.GetDirectoryName(expanded);
                    if (directory is null || !Directory.Exists(directory)) continue;
                    foreach (string file in Directory.GetFiles(directory, Path.GetFileName(expanded)).Order())
                        ReadAliases(Path.GetFullPath(file), aliases, visited, depth + 1);
                }
            }
        }
    }
}
