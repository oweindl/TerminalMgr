using System.Text;
using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

/// <summary>
/// Short, speaking names of Makyx sessions (note 16, decision of 09.11.2026): the server list shows "ingest-api · Worker" under the project,
/// the tab "ProtyxPulse · ingest-api". Makyx sends the name as <c>kurzname</c> (Makyx-PoC/17); until it does, the tmux window name or the id is used.
/// </summary>
public static partial class MakyxNames
{
    public const int MaxLength = 20;

    /// <summary>Lower case letters, digits and '-', at most 20 characters, starting with a letter or digit.</summary>
    public static bool IsValid(string? name) => name is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(name);

    /// <summary>
    /// What a person typed, turned into a valid name: lower case, umlauts spelled out, spaces and '_' become '-', other characters dropped,
    /// repeated '-' joined, cut to 20 characters. Null when nothing is left.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = new StringBuilder();
        foreach (char raw in input.Trim().ToLowerInvariant())
        {
            string part = raw switch { 'ä' => "ae", 'ö' => "oe", 'ü' => "ue", 'ß' => "ss", ' ' or '_' or '.' or '/' => "-", _ => raw.ToString() };
            foreach (char c in part)
                if (char.IsAsciiLetterOrDigit(c) || (c == '-' && text.Length > 0 && text[^1] != '-')) text.Append(c);
        }
        string result = text.ToString().Trim('-');
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd('-');
        return result.Length == 0 ? null : result;
    }

    /// <summary>The short name of a run: kurzname from Makyx, else the tmux window name (not a bare id or index), else the id.</summary>
    public static string ShortName(MakyxRun run)
    {
        if (run.ShortName is { } name && IsValid(name)) return name;
        if (run.Terminal.TmuxWindow is { Length: > 0 } window && window[0] != '@' && !window.All(char.IsAsciiDigit)) return window;
        return run.Id;
    }

    /// <summary>The project as people read it: projekt_name, else the project id, else "freie Session".</summary>
    public static string ProjectLabel(MakyxRun run) =>
        run.ProjectName ?? (run.Project.Length > 0 ? run.Project : run.Kind == "session" ? "freie Session" : "ohne Projekt");

    /// <summary>"Worker" for "worker"; null without a role.</summary>
    public static string? RoleText(MakyxRun run) => run.Role is { Length: > 0 } role ? char.ToUpperInvariant(role[0]) + role[1..] : null;

    /// <summary>Name of the tab and of the terminal entry: "Projekt · Kurzname".</summary>
    public static string TabName(MakyxRun run) => ProjectLabel(run) + " · " + ShortName(run);

    /// <summary>The line of a session under its project: "ingest-api · Worker".</summary>
    public static string RowText(MakyxRun run) => ShortName(run) + (RoleText(run) is { } role ? " · " + role : "");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Pattern();
}

/// <summary>One project of the runs panel: its active sessions and runs, and how many of its runs failed among the recent ones.</summary>
public sealed record MakyxProjectGroup(string Key, string Name, MakyxProject? Project, IReadOnlyList<MakyxRun> Active, int Recent, int Failed)
{
    public int Waiting => Active.Count(r => r.Status == MakyxStatus.Waiting);
}

/// <summary>Groups the runs of the dashboard by project (note 16, mockup A).</summary>
public static class MakyxGrouping
{
    /// <summary>
    /// One group per project that has runs or is known from <c>/projekte</c>: active runs (waiting first) under it; projects with active runs first,
    /// then by name. Runs without a project form the group "freie Session".
    /// </summary>
    public static IReadOnlyList<MakyxProjectGroup> Groups(IReadOnlyList<MakyxRun> runs, IReadOnlyList<MakyxProject> projects)
    {
        var groups = new Dictionary<string, (string Name, MakyxProject? Project, List<MakyxRun> Runs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
            groups[project.Id] = (project.Name, project, []);
        foreach (var run in runs)
        {
            string key = run.Project.Length > 0 ? run.Project : "";
            if (!groups.TryGetValue(key, out var group))
            {
                var byName = projects.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                if (byName is not null && groups.TryGetValue(byName.Id, out group)) key = byName.Id;
                else groups[key] = group = (MakyxNames.ProjectLabel(run), null, []);
            }
            group.Runs.Add(run);
        }
        return groups.Select(g => new MakyxProjectGroup(g.Key, g.Value.Name, g.Value.Project,
                g.Value.Runs.Where(r => r.IsActive).OrderBy(r => r.Status == MakyxStatus.Waiting ? 0 : 1).ThenBy(r => MakyxNames.ShortName(r), StringComparer.Ordinal).ToList(),
                g.Value.Runs.Count(r => !r.IsActive), g.Value.Runs.Count(r => r.Status == MakyxStatus.Failed)))
            .OrderByDescending(g => g.Active.Count > 0).ThenBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The finished runs, newest first, at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<MakyxRun> Recent(IReadOnlyList<MakyxRun> runs, int limit) =>
        runs.Where(r => !r.IsActive).OrderByDescending(r => r.End ?? r.Changed ?? r.Start ?? DateTimeOffset.MinValue).Take(limit).ToList();

    /// <summary>The count next to a project: "2 Läufe", "3 fehlgeschl. · 12", "keine".</summary>
    public static string Summary(MakyxProjectGroup group)
    {
        if (group.Active.Count > 0) return group.Active.Count == 1 ? "1 aktiv" : group.Active.Count + " aktiv";
        if (group.Failed > 0) return group.Failed + " fehlgeschl. · " + group.Recent;
        return group.Recent > 0 ? group.Recent + " beendet" : "keine";
    }
}
