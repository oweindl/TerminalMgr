using System.Text.Json;

namespace TerminalMgr.Core;

/// <summary>What one change means for the user interface.</summary>
public sealed record MakyxChange(bool Changed, IReadOnlyList<MakyxWaiting> NewWaiting);

/// <summary>Holds the last known runs and waiting items and merges snapshots and stream events. The first snapshot is silent: what already waits is not announced again, only what is new.</summary>
public sealed class MakyxState
{
    public const int MaxRuns = 30;
    private readonly Dictionary<string, MakyxRun> _runs = [];
    private readonly Dictionary<string, MakyxWaiting> _waiting = [];
    private readonly HashSet<string> _announced = [];
    private bool _snapshotSeen;
    public DateTimeOffset? LastTime { get; private set; }
    public IReadOnlyCollection<MakyxWaiting> Waiting => _waiting.Values;
    public int WaitingCount => _waiting.Count;

    public void Clear() { _runs.Clear(); _waiting.Clear(); _announced.Clear(); _snapshotSeen = false; LastTime = null; }

    /// <summary>Takes a full answer (runs and waiting items). New waiting items after the first snapshot are returned for a notification.</summary>
    public MakyxChange LoadSnapshot(IReadOnlyList<MakyxRun> runs, IReadOnlyList<MakyxWaiting> waiting, DateTimeOffset? time)
    {
        foreach (var run in runs) _runs[run.Id] = run;
        var fresh = new List<MakyxWaiting>();
        var ids = waiting.Select(w => w.Id).ToHashSet();
        foreach (string gone in _waiting.Keys.Where(k => !ids.Contains(k)).ToList()) { _waiting.Remove(gone); _announced.Remove(gone); }
        foreach (var w in waiting)
        {
            _waiting[w.Id] = w;
            if (_announced.Add(w.Id) && _snapshotSeen) fresh.Add(w);
        }
        _snapshotSeen = true;
        if (time is { } t) LastTime = t;
        return new(true, fresh);
    }

    /// <summary>Takes a catch-up answer (<c>?seit=</c>): only the changed runs; the waiting list is always fetched in full by the caller.</summary>
    public void MergeRuns(IReadOnlyList<MakyxRun> runs, DateTimeOffset? time)
    {
        foreach (var run in runs) _runs[run.Id] = run;
        if (time is { } t) LastTime = t;
    }

    public MakyxChange Apply(SseEvent e)
    {
        try
        {
            switch (e.Name)
            {
                case "lauf": { var run = MakyxJson.ParseRunJson(e.Data); _runs[run.Id] = run; return new(true, []); }
                case "wartet":
                {
                    var w = MakyxJson.ParseWaitingJson(e.Data);
                    _waiting[w.Id] = w;
                    return new(true, _announced.Add(w.Id) && _snapshotSeen ? [w] : []);
                }
                case "wartet_weg":
                {
                    if (MakyxJson.ParseId(e.Data) is { } id) { _waiting.Remove(id); _announced.Remove(id); }
                    return new(true, []);
                }
            }
        }
        catch (JsonException) { /* one broken event must not stop the stream */ }
        return new(false, []);
    }

    /// <summary>Waiting first, then working, then the rest by start (newest first); at most <see cref="MaxRuns"/>.</summary>
    public IReadOnlyList<MakyxRun> Ordered(int max = MaxRuns) => _runs.Values
        .OrderBy(r => r.Status switch { MakyxStatus.Waiting => 0, MakyxStatus.Working => 1, _ => 2 })
        .ThenByDescending(r => r.Start ?? DateTimeOffset.MinValue)
        .Take(max).ToList();

    public int WaitingRunCount => _runs.Values.Count(r => r.Status == MakyxStatus.Waiting);
}
