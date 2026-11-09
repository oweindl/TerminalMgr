using System.Globalization;
using System.Text.Json;

namespace TerminalMgr.Core;

/// <summary>Status of a Makyx run or job. The same five states as in Makyx (dot and text).</summary>
public enum MakyxStatus { Working, Waiting, Done, Failed, Blocked, Unknown }

/// <summary>Where the instance of a run lives as a terminal. Structured sessions have none (Available false, Reason "strukturiert").</summary>
public sealed record MakyxTerminal(bool Available, string? Reason, string? Machine, string? SshTarget, string? TmuxSocket, string? TmuxSession, string? TmuxWindow)
{
    public static MakyxTerminal None(string reason) => new(false, reason, null, null, null, null, null);
}

public sealed record MakyxRun(string Id, string Uuid, string Kind, string Title, MakyxStatus Status, string Project, string? Machine, DateTimeOffset? Start,
    DateTimeOffset? End, int? DurationSeconds, double? CostUsd, string Link, DateTimeOffset? Changed, MakyxTerminal Terminal)
{
    public bool IsActive => Status is MakyxStatus.Working or MakyxStatus.Waiting;
}

public sealed record MakyxWaiting(string Id, string Kind, string SubKind, string Title, string? Project, DateTimeOffset? Since, int? AgeSeconds, string Link, string? Reference);

public sealed record MakyxMachine(string Id, string Name, string Status, bool Connected, string? Version, string? SshTarget, string? TmuxSocket);

public sealed record MakyxHello(string Version, string Vault, string Client);

/// <summary>Reads the JSON of the Makyx dashboard API (v1). Unknown fields are ignored; a missing field never throws, a broken document does (JsonException).</summary>
public static class MakyxJson
{
    public static MakyxStatus ParseStatus(string? value) => value switch
    {
        "arbeitet" => MakyxStatus.Working, "wartet" => MakyxStatus.Waiting, "fertig" => MakyxStatus.Done,
        "fehlgeschlagen" => MakyxStatus.Failed, "blockiert" => MakyxStatus.Blocked, _ => MakyxStatus.Unknown
    };

    public static string StatusText(MakyxStatus status) => status switch
    {
        MakyxStatus.Working => "arbeitet", MakyxStatus.Waiting => "wartet", MakyxStatus.Done => "fertig",
        MakyxStatus.Failed => "fehlgeschlagen", MakyxStatus.Blocked => "blockiert", _ => "unbekannt"
    };

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    private static double? Dbl(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>Only http and https links are ever opened: the server could otherwise hand out a file: or other scheme.</summary>
    public static string SafeLink(string? link) => Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.UserInfo.Length == 0 ? u.AbsoluteUri : "";

    public static MakyxTerminal ParseTerminal(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return MakyxTerminal.None("unbekannt");
        if (!Bool(e, "vorhanden")) return new(false, Str(e, "grund") ?? "unbekannt", Str(e, "rechner"), Str(e, "ssh_ziel"), null, null, null);
        return new(true, null, Str(e, "rechner"), Str(e, "ssh_ziel"), Str(e, "tmux_socket"), Str(e, "tmux_session"), Str(e, "tmux_fenster"));
    }

    public static MakyxRun ParseRun(JsonElement e) => new(Str(e, "id") ?? "?", Str(e, "uuid") ?? Str(e, "id") ?? "?", Str(e, "art") ?? "", Str(e, "titel") ?? "", ParseStatus(Str(e, "status")),
        Str(e, "projekt") ?? "", Str(e, "rechner"), Time(e, "beginn"), Time(e, "ende"), Int(e, "dauer_s"), Dbl(e, "kosten_usd"), SafeLink(Str(e, "link")), Time(e, "geaendert_am"),
        e.TryGetProperty("terminal", out var t) ? ParseTerminal(t) : MakyxTerminal.None("unbekannt"));

    public static MakyxWaiting ParseWaitingItem(JsonElement e) => new(Str(e, "id") ?? "?", Str(e, "art") ?? "", Str(e, "unterart") ?? "", Str(e, "titel") ?? "", Str(e, "projekt"),
        Time(e, "seit"), Int(e, "alter_s"), SafeLink(Str(e, "link")), Str(e, "bezug"));

    public static MakyxMachine ParseMachine(JsonElement e) => new(Str(e, "id") ?? "?", Str(e, "name") ?? "", Str(e, "status") ?? "", Bool(e, "verbunden"), Str(e, "version"), Str(e, "ssh_ziel"),
        e.TryGetProperty("tmux", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "socket") : null);

    private static IReadOnlyList<T> List<T>(string json, string property, Func<JsonElement, T> parse, out DateTimeOffset? time)
    {
        using var doc = JsonDocument.Parse(json);
        time = Time(doc.RootElement, "zeit");
        if (!doc.RootElement.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(parse).ToList();
    }

    public static IReadOnlyList<MakyxRun> ParseRuns(string json, out DateTimeOffset? time) => List(json, "laeufe", ParseRun, out time);
    public static IReadOnlyList<MakyxWaiting> ParseWaiting(string json) => List(json, "wartet", ParseWaitingItem, out _);
    public static IReadOnlyList<MakyxMachine> ParseMachines(string json) => List(json, "rechner", ParseMachine, out _);

    public static MakyxHello ParseHello(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new(Str(doc.RootElement, "schnittstelle") ?? "", Str(doc.RootElement, "vault") ?? "", Str(doc.RootElement, "client") ?? "");
    }

    public static MakyxRun ParseRunJson(string json) { using var doc = JsonDocument.Parse(json); return ParseRun(doc.RootElement); }
    public static MakyxWaiting ParseWaitingJson(string json) { using var doc = JsonDocument.Parse(json); return ParseWaitingItem(doc.RootElement); }
    public static string? ParseId(string json) { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement, "id"); }

    /// <summary>Human text for the kind of a waiting item (toast and list).</summary>
    public static string WaitingKindText(MakyxWaiting w) => w.Kind switch
    {
        "rueckfrage" => "Rückfrage", "entscheidung" => "Entscheidung", "zertifikat" => "Zertifikat",
        _ => w.SubKind == "leerlauf" ? "Leerlauf" : "wartet"
    };
}
