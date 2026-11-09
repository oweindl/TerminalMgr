namespace TerminalMgr.Core;

/// <summary>A project under a machine in the MAKYX tree, with its running instances (only active ones; finished runs live in Makyx).</summary>
public sealed record MakyxTreeProject(string Key, string Name, MakyxProject? Project, IReadOnlyList<MakyxRun> Active, int GatesOpen);

/// <summary>A machine in the MAKYX tree. <see cref="Machine"/> is null for the group "Ohne Rechner" (projects or runs Makyx reports without one).</summary>
public sealed record MakyxTreeMachine(string Key, string Name, MakyxMachine? Machine, bool Connected, IReadOnlyList<MakyxTreeProject> Projects)
{
    /// <summary>Running instances on this machine (each holds a session slot of the runner).</summary>
    public int ActiveCount => Projects.Sum(p => p.Active.Count);
}

/// <summary>
/// Builds the MAKYX tree of the sidebar: machine › project › running instance (note 15). Pure, so the grouping is testable: a project
/// belongs to the machine Makyx names for it, a run to the machine it runs on; runs of a project on another machine show the project
/// there too. Finished runs are left out.
/// </summary>
public static class MakyxTree
{
    public const string NoMachineKey = "-";

    private static bool Same(string? a, string? b) => a is { Length: > 0 } && b is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(MakyxMachine machine, string? name) => Same(machine.Id, name) || Same(machine.Name, name);

    private static bool IsProject(MakyxProject project, string runProject) => Same(project.Name, runProject) || Same(project.Id, runProject);

    public static IReadOnlyList<MakyxTreeMachine> Build(IReadOnlyList<MakyxMachine> machines, IReadOnlyList<MakyxProject> projects, IReadOnlyList<MakyxRun> runs)
    {
        var distinct = machines.GroupBy(MakyxControl.MachineValue, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var active = runs.Where(r => r.IsActive).ToList();
        string MachineKeyOf(string? name) => distinct.FirstOrDefault(m => Matches(m, name)) is { } m ? MakyxControl.MachineValue(m) : name is { Length: > 0 } ? name : NoMachineKey;

        // machine key -> project key -> (name, project, runs)
        var groups = new Dictionary<string, Dictionary<string, (string Name, MakyxProject? Project, List<MakyxRun> Runs)>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Name, MakyxProject? Project, List<MakyxRun> Runs)> Group(string key) =>
            groups.TryGetValue(key, out var g) ? g : groups[key] = new(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
            Group(MachineKeyOf(project.Machine))[project.Id] = (project.Name, project, []);
        foreach (var run in active)
        {
            var projectGroups = Group(MachineKeyOf(run.Terminal.Machine ?? run.Machine));
            var project = projects.FirstOrDefault(p => IsProject(p, run.Project));
            string key = project?.Id ?? (run.Project.Length > 0 ? run.Project : "freie Session");
            if (!projectGroups.TryGetValue(key, out var entry))
                projectGroups[key] = entry = (project?.Name ?? (run.Project.Length > 0 ? run.Project : "Freie Sessions"), project, []);
            entry.Runs.Add(run);
        }
        foreach (var machine in distinct) Group(MakyxControl.MachineValue(machine));

        var result = new List<MakyxTreeMachine>();
        foreach (var (key, projectGroups) in groups)
        {
            var machine = distinct.FirstOrDefault(m => Same(MakyxControl.MachineValue(m), key));
            var list = projectGroups.Select(p => new MakyxTreeProject(key + "/" + p.Key, p.Value.Name, p.Value.Project,
                    p.Value.Runs.OrderBy(r => r.Start ?? DateTimeOffset.MaxValue).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(), p.Value.Project?.GatesOpen ?? 0))
                // Projects with running instances first, then by name; test projects ("[Test] …") after the others.
                .OrderByDescending(p => p.Active.Count > 0).ThenBy(p => p.Name.StartsWith('[')).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.Add(new MakyxTreeMachine(key, key == NoMachineKey ? "Ohne Rechner" : key, machine, machine?.Connected ?? false, list));
        }
        // Connected machines first, the group without machine last.
        return result.OrderBy(m => m.Key == NoMachineKey).ThenByDescending(m => m.Connected).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The item of "Wartet auf dich" that belongs to a run (gate or question), so the tree can offer "Antworten" at the instance.</summary>
    public static MakyxWaiting? WaitingFor(MakyxRun run, IEnumerable<MakyxWaiting> waiting) =>
        waiting.FirstOrDefault(w => MakyxControl.IsAnswerable(w) && (Same(w.Reference, run.Id) || Same(w.Reference, run.Uuid)));

    /// <summary>The line of an instance: "worker-4158" (the tmux window), else the title, else the id.</summary>
    public static string InstanceName(MakyxRun run) => run.Terminal.TmuxWindow is { Length: > 0 } w ? w : run.Title.Length > 0 ? run.Title : run.Id;
}
