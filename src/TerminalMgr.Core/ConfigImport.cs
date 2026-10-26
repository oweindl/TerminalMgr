namespace TerminalMgr.Core;

public enum ImportMode { Merge, ReplaceAll }
public enum ConflictChoice { Skip, Replace, Copy }

/// <param name="Existing">The profile of the same name that is already here, null when the incoming one is new.</param>
public sealed record ProfileImportItem(Profile Incoming, Profile? Existing)
{
    public bool Conflict => Existing is not null;
}

/// <param name="ExistingAliases">Aliases of the block that the SSH config already defines; a block with any of them is a conflict and is never overwritten.</param>
public sealed record HostImportItem(SshHostBlock Block, IReadOnlyList<string> ExistingAliases)
{
    public bool Conflict => ExistingAliases.Count > 0;
}

public sealed record ImportPlan(ImportMode Mode, IReadOnlyList<ProfileImportItem> Profiles, bool HasSettings, IReadOnlyList<ClipboardEntry> NewPinned,
    IReadOnlyList<ClipboardEntry> KnownPinned, IReadOnlyList<HostImportItem> Hosts);

/// <param name="HostBlocksToAdd">Blocks to append to the SSH config (only after the user confirmed and a backup exists).</param>
public sealed record ImportResult(AppSettings Settings, IReadOnlyList<Profile> Added, IReadOnlyList<Profile> Replaced, IReadOnlyList<Profile> Copied,
    IReadOnlyList<string> Skipped, IReadOnlyList<SshHostBlock> HostBlocksToAdd);

/// <summary>Preview and merge of an imported configuration into the current one. Pure logic; files are handled by the caller.</summary>
public static class ConfigImporter
{
    public static ImportPlan BuildPlan(AppSettings current, ConfigContent incoming, IReadOnlyCollection<string> existingHostAliases, ImportMode mode)
    {
        var profiles = (incoming.Profiles ?? []).Select(p => new ProfileImportItem(p,
            current.Profiles.FirstOrDefault(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        var pinned = incoming.PinnedClips ?? [];
        var known = current.PinnedClips.Select(c => c.Text).ToHashSet();
        var hosts = (incoming.SshHosts ?? []).Select(b => new HostImportItem(b,
            b.Aliases.Where(a => SshConfigBlocks.IsExplicitAlias(a) && existingHostAliases.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList())).ToList();
        return new(mode, profiles, incoming.Settings is not null, pinned.Where(c => !known.Contains(c.Text)).ToList(), pinned.Where(c => known.Contains(c.Text)).ToList(), hosts);
    }

    /// <param name="choices">Per conflicting profile (by the incoming profile's Id) what to do; missing entries count as Skip.</param>
    public static ImportResult Apply(AppSettings current, ConfigContent incoming, ImportPlan plan, IReadOnlyDictionary<string, ConflictChoice> choices,
        bool takeProfiles, bool takeSettings, bool takePinned, bool takeHosts)
    {
        var profiles = current.Profiles.ToList();
        List<Profile> added = [], replaced = [], copied = [];
        List<string> skipped = [];
        if (takeProfiles && incoming.Profiles is not null)
        {
            if (plan.Mode == ImportMode.ReplaceAll)
            {
                // Everything is replaced by the file's profiles; profiles of the same name keep their Id so that nothing refers to a vanished one.
                var replacement = incoming.Profiles.Select(p => plan.Profiles.First(i => ReferenceEquals(i.Incoming, p)) is { Existing: { } e } ? p with { Id = e.Id } : p).ToList();
                added.AddRange(replacement.Where(p => !current.Profiles.Any(c => c.Id == p.Id)));
                replaced.AddRange(replacement.Where(p => current.Profiles.Any(c => c.Id == p.Id)));
                profiles = replacement;
            }
            else
            {
                foreach (var item in plan.Profiles)
                {
                    if (!item.Conflict) { profiles.Add(item.Incoming with { Id = NewId(profiles, item.Incoming.Id) }); added.Add(item.Incoming); continue; }
                    switch (choices.GetValueOrDefault(item.Incoming.Id, ConflictChoice.Skip))
                    {
                        case ConflictChoice.Replace:
                            int index = profiles.FindIndex(p => p.Id == item.Existing!.Id);
                            var replacedProfile = item.Incoming with { Id = item.Existing!.Id };
                            if (index >= 0) profiles[index] = replacedProfile; else profiles.Add(replacedProfile);
                            replaced.Add(replacedProfile);
                            break;
                        case ConflictChoice.Copy:
                            var copy = item.Incoming with { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(profiles, item.Incoming.Name + " (Kopie)") };
                            profiles.Add(copy); copied.Add(copy);
                            break;
                        default: skipped.Add(item.Incoming.Name); break;
                    }
                }
            }
        }
        var result = current with { Profiles = profiles };
        if (takeSettings && incoming.Settings is { } s)
            result = result with {
                FontSize = Math.Clamp(s.FontSize, 10, 28), ClipHistoryLimit = Math.Clamp(s.ClipHistoryLimit, 1, 1000), AcceptOsc52 = s.AcceptOsc52,
                Shortcuts = s.Shortcuts.Normalized(),
                // Favorites: replaced entirely in "replace all", otherwise the incoming ones are added (an entry with the same ID is replaced).
                Favorites = plan.Mode == ImportMode.ReplaceAll ? s.Favorites.ToList()
                    : result.Favorites.Where(f => s.Favorites.All(i => i.Id != f.Id)).Concat(s.Favorites).ToList(),
                Update = result.Update with { CheckOnStart = s.CheckForUpdates },
                // The model, the microphone and the Windows start stay as they are on this machine.
                Dictation = (result.Dictation with { Shortcut = s.Dictation.Shortcut, Language = s.Dictation.Language, InitialPrompt = s.Dictation.InitialPrompt,
                    SilenceSeconds = s.Dictation.SilenceSeconds, LevelThreshold = s.Dictation.LevelThreshold }).Normalized()
            };
        if (takePinned && incoming.PinnedClips is { } pinned)
        {
            var kept = plan.Mode == ImportMode.ReplaceAll ? [] : result.PinnedClips.ToList();
            foreach (var clip in pinned.Where(c => c.Pinned && c.Text.Length > 0 && c.Text.Length <= ClipboardList.MaxTextLength))
                if (kept.All(k => k.Text != clip.Text)) kept.Add(clip with { Pinned = true });
            result = result with { PinnedClips = kept };
        }
        var hosts = takeHosts ? plan.Hosts.Where(h => !h.Conflict).Select(h => h.Block).ToList() : [];
        return new(result, added, replaced, copied, skipped, hosts);
    }

    private static string NewId(List<Profile> existing, string id) => existing.Any(p => p.Id == id) ? Guid.NewGuid().ToString("N") : id;

    private static string UniqueName(List<Profile> existing, string name)
    {
        string candidate = name;
        for (int i = 2; existing.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} {i}";
        return candidate;
    }
}

/// <summary>Backups that exist before an import changes anything.</summary>
public static class ConfigBackup
{
    /// <summary>Copies settings.json to backup\settings-&lt;time&gt;.json; returns null when there is no settings file yet.</summary>
    public static string? BackupSettings(string dataDirectory, DateTimeOffset now)
    {
        string source = Path.Combine(dataDirectory, "settings.json");
        if (!File.Exists(source)) return null;
        string directory = Path.Combine(dataDirectory, "backup");
        Directory.CreateDirectory(directory);
        string target = UniquePath(Path.Combine(directory, $"settings-{now:yyyyMMdd-HHmmss}"), ".json");
        File.Copy(source, target, overwrite: false);
        return target;
    }

    /// <summary>A backup never overwrites an older one, not even from the same second.</summary>
    private static string UniquePath(string stem, string extension)
    {
        string candidate = stem + extension;
        for (int i = 2; File.Exists(candidate); i++) candidate = $"{stem}-{i}{extension}";
        return candidate;
    }

    /// <summary>Copies the SSH config to config.bak-&lt;time&gt; next to it; returns null when it does not exist.</summary>
    public static string? BackupSshConfig(string configPath, DateTimeOffset now)
    {
        if (!File.Exists(configPath)) return null;
        string target = UniquePath(configPath + $".bak-{now:yyyyMMdd-HHmmss}", "");
        File.Copy(configPath, target, overwrite: false);
        return target;
    }
}
