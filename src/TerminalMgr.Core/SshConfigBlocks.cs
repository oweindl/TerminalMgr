using System.Text;
using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

/// <summary>Host blocks of an OpenSSH config: read them (including Include files), pick those of the exported profiles, append imported ones.</summary>
public static partial class SshConfigBlocks
{
    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'|([^\\s]+)")]
    private static partial Regex Tokens();

    public static string DefaultConfigPath(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>All Host blocks of the file and of the files it includes. A Match block ends a Host block and is not exported.</summary>
    public static IReadOnlyList<SshHostBlock> Read(string path, string? home = null)
    {
        var blocks = new List<SshHostBlock>();
        ReadFile(Path.GetFullPath(path), home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), blocks, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return blocks;
    }

    private static void ReadFile(string path, string home, List<SshHostBlock> blocks, HashSet<string> visited, int depth)
    {
        if (depth > 16 || !visited.Add(path) || !File.Exists(path)) return;
        List<string>? current = null;
        List<string> aliases = [];
        void Close()
        {
            if (current is null) return;
            while (current.Count > 0 && string.IsNullOrWhiteSpace(current[^1])) current.RemoveAt(current.Count - 1);
            if (aliases.Count > 0) blocks.Add(new(aliases.ToList(), string.Join("\n", current)));
            current = null;
        }
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            var (key, value) = Split(trimmed);
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                aliases = Words(value).ToList();
                current = [line.TrimStart()];
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase)) { Close(); aliases = []; }
            else if (key.Equals("Include", StringComparison.OrdinalIgnoreCase) && current is null)
            {
                foreach (string include in Words(value))
                {
                    string expanded = include.StartsWith("~/") ? Path.Combine(home, include[2..]) : include;
                    if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(home, ".ssh", expanded);
                    string? directory = Path.GetDirectoryName(expanded);
                    if (directory is null || !Directory.Exists(directory)) continue;
                    foreach (string file in Directory.GetFiles(directory, Path.GetFileName(expanded)).Order())
                        ReadFile(Path.GetFullPath(file), home, blocks, visited, depth + 1);
                }
            }
            else current?.Add(line);
        }
        Close();
    }

    private static (string Key, string Value) Split(string line)
    {
        if (line.Length == 0 || line.StartsWith('#')) return ("", "");
        int split = line.IndexOfAny([' ', '\t', '=']);
        return split < 0 ? (line, "") : (line[..split], line[(split + 1)..].TrimStart(' ', '\t', '='));
    }

    private static IEnumerable<string> Words(string value) =>
        Tokens().Matches(value).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            .TakeWhile(t => !t.StartsWith('#'));

    public static bool IsExplicitAlias(string alias) => alias.Length > 0 && !alias.Any(c => "*?!".Contains(c)) && !alias.StartsWith('-');

    /// <summary>The blocks that define the destinations of the given profiles (each block once), read from each profile's own config or the default one.</summary>
    public static IReadOnlyList<SshHostBlock> ForProfiles(IEnumerable<Profile> profiles, string? defaultConfig = null, string? home = null)
    {
        var result = new List<SshHostBlock>();
        var seen = new HashSet<string>();
        var cache = new Dictionary<string, IReadOnlyList<SshHostBlock>>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            string config = string.IsNullOrWhiteSpace(profile.ConfigPath) ? defaultConfig ?? DefaultConfigPath(home) : profile.ConfigPath;
            if (!cache.TryGetValue(config, out var blocks)) cache[config] = blocks = Read(config, home);
            foreach (var block in blocks.Where(b => b.Aliases.Any(a => IsExplicitAlias(a) && a.Equals(profile.Destination, StringComparison.OrdinalIgnoreCase))))
                if (seen.Add(block.Text)) result.Add(block);
        }
        return result;
    }

    /// <summary>The key files named by IdentityFile lines of the blocks; they are not exported and have to be copied separately.</summary>
    public static IReadOnlyList<string> IdentityFiles(IEnumerable<SshHostBlock> blocks) =>
        blocks.SelectMany(b => b.Text.Split('\n')).Select(l => Split(l.Trim()))
            .Where(p => p.Key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase)).Select(p => Words(p.Value).FirstOrDefault() ?? "")
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every explicit alias that is already defined in the config (and its includes).</summary>
    public static IReadOnlySet<string> ExistingAliases(string path, string? home = null) =>
        Read(path, home).SelectMany(b => b.Aliases).Where(IsExplicitAlias).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The config text with the imported blocks appended after a marker line; existing text is never changed.</summary>
    public static string AppendBlocks(string existingText, IEnumerable<SshHostBlock> blocks, DateTimeOffset now)
    {
        string newline = existingText.Contains("\r\n") ? "\r\n" : "\n";
        var result = new StringBuilder(existingText);
        if (existingText.Length > 0 && !existingText.EndsWith('\n')) result.Append(newline);
        result.Append(newline).Append("# Importiert von TerminalMgr am ").Append(now.ToString("yyyy-MM-dd")).Append(newline);
        foreach (var block in blocks) result.Append(block.Text.Replace("\n", newline)).Append(newline).Append(newline);
        return result.ToString();
    }
}
