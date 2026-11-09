using System.Text.Json;

namespace TerminalMgr.Core;

/// <summary>A Makyx project (GET projekte): where it lives, how many instances run and how many gates wait.</summary>
public sealed record MakyxProject(string Id, string Name, string? Repository, string? Source, string? Machine, int Running, int GatesOpen, string Link);

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

    /// <summary>POST sessions: {projekt, rechner?, agent, modell?, modus: "terminal"}.</summary>
    public static string SessionBody(string project, string? machine, string? agent, string? model)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Projekt fehlt.");
        string runtime = agent is { Length: > 0 } a && Agents.Contains(a) ? a : Agents[0];
        return Json(w =>
        {
            w.WriteString("projekt", project.Trim());
            if (machine is { Length: > 0 } m && !string.IsNullOrWhiteSpace(m)) w.WriteString("rechner", m.Trim());
            w.WriteString("agent", runtime);
            if (model is { Length: > 0 } mo && !string.IsNullOrWhiteSpace(mo)) w.WriteString("modell", mo.Trim());
            w.WriteString("modus", "terminal");
        });
    }

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

    /// <summary>The menu text of a project: "Name · 2 laufen · 1 Gate".</summary>
    public static string ProjectText(MakyxProject p)
    {
        var parts = new List<string> { p.Name };
        if (p.Running > 0) parts.Add(p.Running == 1 ? "1 läuft" : p.Running + " laufen");
        if (p.GatesOpen > 0) parts.Add(p.GatesOpen == 1 ? "1 Gate" : p.GatesOpen + " Gates");
        return string.Join(" · ", parts);
    }
}
