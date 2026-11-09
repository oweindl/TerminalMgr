using System.Text.Json;

namespace TerminalMgr.Core;

/// <summary>A Makyx project (GET projekte): where it lives, how many instances run and how many gates wait.</summary>
/// <param name="LastActive">When the project was last active (zuletzt_aktiv), for sorting the list of "Neue Session".</param>
/// <param name="DefaultModel">The model Makyx uses for the project when none is chosen.</param>
public sealed record MakyxProject(string Id, string Name, string? Repository, string? Source, string? Machine, int Running, int GatesOpen, string Link,
    DateTimeOffset? LastActive = null, string? DefaultModel = null);

/// <summary>Where a new session works: a new git worktree (default) or the main folder of the project.</summary>
public enum MakyxFolder { Worktree, Main }

/// <summary>Answer to a gate or a question (POST wartet/{id}/antwort).</summary>
public enum MakyxDecision { Approve, Reject, Return }

/// <summary>
/// The writing part of the Makyx dashboard API (note Makyx-PoC/13 C, right "steuern"): request bodies, ids in paths and readable error texts.
/// Pure functions, so the rules are testable without a server. Every action runs as the person who created the access in Makyx and is logged there.
/// </summary>
public static class MakyxControl
{
    /// <summary>Runtimes Makyx accepts for a new session; the first is the default.</summary>
    public static readonly IReadOnlyList<string> Agents = ["claude", "codex"];
    public const int MaxRunItems = 50;

    public static string DecisionValue(MakyxDecision decision) => decision switch
    {
        MakyxDecision.Approve => "freigeben", MakyxDecision.Reject => "ablehnen", _ => "zurueckgeben"
    };

    /// <summary>An id for a path segment (wartet/{id}, laeufe/{id}): only letters, digits, '-' and '_'; anything else is refused.</summary>
    public static string PathId(string? id)
    {
        if (id is not { Length: > 0 and <= 100 } || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("Ungültige Kennung von Makyx.");
        return id;
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// POST sessions: {projekt, rechner?, agent, modell?, modus: "terminal", name?, arbeitsordner?, erste_nachricht?}. The last three are new
    /// (Makyx-PoC/17); they are only sent when set, so an older Makyx sees the same body as before. The name is normalized
    /// (<see cref="MakyxNames.Normalize"/>); a name that stays empty is left out (Makyx then names the session itself).
    /// </summary>
    public static string SessionBody(string project, string? machine, string? agent, string? model,
        string? name = null, MakyxFolder? folder = null, string? firstMessage = null)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Projekt fehlt.");
        string runtime = agent is { Length: > 0 } a && Agents.Contains(a) ? a : Agents[0];
        string? shortName = MakyxNames.Normalize(name);
        string? message = string.IsNullOrWhiteSpace(firstMessage) ? null : firstMessage.Trim();
        if (message is { Length: > 20_000 }) throw new ArgumentException("Die erste Nachricht ist zu lang.");
        return Json(w =>
        {
            w.WriteString("projekt", project.Trim());
            if (machine is { Length: > 0 } m && !string.IsNullOrWhiteSpace(m)) w.WriteString("rechner", m.Trim());
            w.WriteString("agent", runtime);
            if (model is { Length: > 0 } mo && !string.IsNullOrWhiteSpace(mo)) w.WriteString("modell", mo.Trim());
            w.WriteString("modus", "terminal");
            if (shortName is not null) w.WriteString("name", shortName);
            if (folder is { } f) w.WriteString("arbeitsordner", f == MakyxFolder.Main ? "haupt" : "worktree");
            if (message is not null) w.WriteString("erste_nachricht", message);
        });
    }

    /// <summary>PATCH sessions/{id}: {name} (rename; Makyx renames the tmux window with it).</summary>
    public static string RenameBody(string? name) =>
        MakyxNames.Normalize(name) is { } shortName ? Json(w => w.WriteString("name", shortName)) : throw new ArgumentException("Bitte einen Namen angeben (Buchstaben, Ziffern und -).");

    /// <summary>POST wartet/{id}/antwort: {entscheidung, text?}. "Zurückgeben" needs a text (it goes back to the agent).</summary>
    public static string AnswerBody(MakyxDecision decision, string? text)
    {
        string? trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (decision == MakyxDecision.Return && trimmed is null) throw new ArgumentException("Zum Zurückgeben bitte eine Antwort schreiben.");
        if (trimmed is { Length: > 20_000 }) throw new ArgumentException("Die Antwort ist zu lang.");
        return Json(w => { w.WriteString("entscheidung", DecisionValue(decision)); if (trimmed is not null) w.WriteString("text", trimmed); });
    }

    /// <summary>The points of a run, one per line (backlog id or text of a new point); empty lines are dropped.</summary>
    public static IReadOnlyList<string> RunItems(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>POST laeufe: {projekt, punkte[]}.</summary>
    public static string StartRunBody(string project, IReadOnlyList<string> items)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Projekt fehlt.");
        if (items.Count == 0) throw new ArgumentException("Bitte mindestens einen Punkt angeben (eine Zeile je Punkt).");
        if (items.Count > MaxRunItems) throw new ArgumentException($"Höchstens {MaxRunItems} Punkte je Lauf.");
        return Json(w =>
        {
            w.WriteString("projekt", project.Trim());
            w.WriteStartArray("punkte"); foreach (var item in items) w.WriteStringValue(item); w.WriteEndArray();
        });
    }

    /// <summary>POST laeufe/{id}/nothalt needs the explicit confirmation (it stops all of Makyx).</summary>
    public const string EmergencyBody = "{\"bestaetigt\":true}";

    /// <summary>Readable text for a refused action; the body may carry {"fehler": "...", "meldung": "..."}.</summary>
    public static string ErrorText(int status, string? body)
    {
        string? code = null, message = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && body.Length < 20_000)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    code = Text(doc.RootElement, "fehler") ?? Text(doc.RootElement, "error") ?? Text(doc.RootElement, "code");
                    message = Text(doc.RootElement, "meldung") ?? Text(doc.RootElement, "message");
                }
            }
        }
        catch (JsonException) { }
        if (code == "kein_recht_steuern") return "Dieser Zugang darf nicht steuern. In Makyx unter Leitstand-Zugänge einen Zugang mit Recht „steuern“ anlegen und TerminalMgr neu koppeln.";
        if (code == "nur_lesen") return "Dieser Zugang darf nur lesen.";
        string detail = message is { Length: > 0 and <= 300 } ? ": " + message : code is { Length: > 0 and <= 100 } ? " (" + code + ")" : "";
        return status switch
        {
            400 => "Makyx lehnt die Anfrage ab" + detail,
            403 => "Makyx erlaubt diese Aktion nicht" + detail,
            404 => "Makyx kennt diesen Eintrag nicht (mehr)" + detail,
            409 => "Das geht gerade nicht" + detail,
            _ => $"Makyx antwortet mit {status}" + detail
        };
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Can this waiting item be answered from TerminalMgr? Gates (entscheidung) and questions (rueckfrage); certificates and idle notes not.</summary>
    public static bool IsAnswerable(MakyxWaiting item) => item.Kind is "entscheidung" or "rueckfrage";

    /// <summary>Can this run be stopped? Active runs and jobs; free sessions are ended in their terminal.</summary>
    public static bool IsStoppable(MakyxRun run) => run.IsActive && run.Kind != "session";

    /// <summary>Name of the terminal entry and tab of a Makyx instance: "Projekt · Kurzname" (note 16), short enough for the server list.</summary>
    public static string TerminalName(MakyxRun run) => MakyxNames.TabName(run);

    /// <summary>What "Neue Session" sends as rechner: the machine's name, its id when it has none.</summary>
    public static string MachineValue(MakyxMachine machine) => machine.Name.Length > 0 ? machine.Name : machine.Id;

    /// <summary>The line in the machine list of "Neue Session", with free memory when Makyx reports it ("linuxdev · RAM 9,8 / 16 GB frei").</summary>
    public static string MachineLabel(MakyxMachine machine) => MachineValue(machine) + (machine.Connected ? "" : " · nicht verbunden")
        + (machine.RamFreeMb is { } free ? " · RAM " + Gb(free) + (machine.RamTotalMb is { } total ? " / " + Gb(total) : "") + " GB frei" : "");

    private static string Gb(int mb) => (mb / 1024.0).ToString(mb % 1024 == 0 ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

    /// <summary>Projects for the list of "Neue Session": active ones first, then the most recently used, then by name.</summary>
    public static IReadOnlyList<MakyxProject> SortProjects(IEnumerable<MakyxProject> projects) =>
        projects.OrderByDescending(p => p.Running > 0).ThenByDescending(p => p.LastActive ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The detail line of a project in that list: "2 Läufe aktiv · zuletzt vor 33 min".</summary>
    public static string ProjectDetail(MakyxProject p, DateTimeOffset now)
    {
        var parts = new List<string> { p.Running switch { 0 => "keine aktiv", 1 => "1 Lauf aktiv", _ => p.Running + " Läufe aktiv" } };
        if (p.GatesOpen > 0) parts.Add(p.GatesOpen == 1 ? "1 Gate" : p.GatesOpen + " Gates");
        if (p.LastActive is { } last) parts.Add("zuletzt " + Ago(now - last));
        return string.Join(" · ", parts);
    }

    public static string Ago(TimeSpan span) => span.TotalSeconds < 60 ? "gerade eben" : span.TotalMinutes < 60 ? $"vor {(int)span.TotalMinutes} min"
        : span.TotalHours < 24 ? $"vor {(int)span.TotalHours} h" : $"vor {(int)span.TotalDays} Tagen";

    /// <summary>The menu text of a project: "Name · 2 laufen · 1 Gate".</summary>

    /// <summary>
    /// The machine "Neue Session" preselects: the project's machine (by id or name), otherwise the only connected one, otherwise the
    /// first; -1 when Makyx reports none (Makyx then chooses).
    /// </summary>
    public static int PreferredMachine(IReadOnlyList<MakyxMachine> machines, string? projectMachine)
    {
        if (machines.Count == 0) return -1;
        if (projectMachine is { Length: > 0 })
            for (int i = 0; i < machines.Count; i++)
                if (string.Equals(machines[i].Id, projectMachine, StringComparison.OrdinalIgnoreCase) || string.Equals(machines[i].Name, projectMachine, StringComparison.OrdinalIgnoreCase)) return i;
        var connected = machines.Select((m, i) => (m, i)).Where(x => x.m.Connected).ToList();
        return connected.Count == 1 ? connected[0].i : 0;
    }

    public static string ProjectText(MakyxProject p)
    {
        var parts = new List<string> { p.Name };
        if (p.Running > 0) parts.Add(p.Running == 1 ? "1 läuft" : p.Running + " laufen");
        if (p.GatesOpen > 0) parts.Add(p.GatesOpen == 1 ? "1 Gate" : p.GatesOpen + " Gates");
        return string.Join(" · ", parts);
    }
}
