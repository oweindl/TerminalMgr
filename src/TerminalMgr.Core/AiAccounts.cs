using System.Globalization;
using System.Text.Json;

namespace TerminalMgr.Core;

public sealed record AiFact(string Name, string Value);
public sealed record AiQuotaWindow(string Bucket, string Label, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public bool Expired(DateTimeOffset now) => ResetsAt <= now;
    public string ResetText(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset) return "Reset unbekannt";
        var left = reset - now;
        if (left <= TimeSpan.Zero) return "Update ausstehend";
        return left.TotalDays >= 1 ? $"↻ {(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"↻ {(int)left.TotalHours}h {left.Minutes:00}m" : $"↻ {Math.Max(1, (int)left.TotalMinutes)}m";
    }
}

/// <summary>Safe display metadata only; never a raw auth response or credential.</summary>
public sealed record AiAccountSnapshot(string Provider, string Plan, string Status, DateTimeOffset? ObservedAt,
    IReadOnlyList<AiQuotaWindow> Quotas, IReadOnlyList<AiFact> Facts)
{
    public bool NeedsCollector { get; init; }
    public string? Error { get; init; }
    public bool IsStale(DateTimeOffset now) => ObservedAt is { } observed && now - observed > TimeSpan.FromMinutes(5);
    public static AiAccountSnapshot Unavailable(string provider, string message) => new(provider, "", message, null, [], []);
}

public interface IAiAccountSource
{
    Task<IReadOnlyList<AiAccountSnapshot>> ReadAsync(Profile? profile, CancellationToken cancellationToken);
}

/// <summary>Allowlisted provider fields. Unknown/missing windows stay absent; never interpreted as zero.</summary>
public static class AiAccountParser
{
    public static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    public static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? Clean(value.GetString()!) : null;
    public static string? Text(JsonElement value, string name) => Text(Get(value, name));
    public static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
    public static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).Take(300).ToArray());
    public static DateTimeOffset? Epoch(JsonElement value)
    {
        if (Number(value) is not { } n || n < -62135596800 || n > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)n);
    }
    private static void Field(List<AiFact> facts, JsonElement obj, string key, string label)
    {
        var v = Get(obj, key);
        string? text = Text(v) ?? (Number(v) is { } n ? n.ToString("N2", CultureInfo.CurrentCulture) : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() ? "Ja" : "Nein" : null);
        if (text is not null) facts.Add(new(label, text));
    }
    private static void Window(List<AiQuotaWindow> quotas, JsonElement window, string bucket, string label, string percent, string reset)
    {
        if (Number(Get(window, percent)) is { } used && used is >= 0 and <= 100)
            quotas.Add(new(bucket, label, used, Epoch(Get(window, reset))));
    }

    public static AiAccountSnapshot Codex(JsonElement accountResult, JsonElement limits, JsonElement usage, JsonElement notices, DateTimeOffset observed)
    {
        var account = Get(accountResult, "account");
        string type = Text(account, "type") ?? "none";
        bool subscription = type is "chatgpt" or "chatgptAuthTokens";
        var facts = new List<AiFact> { new("Anmeldung", type), new("Quelle", "Codex app-server"), new("Geltungsbereich", "Codex-Konto; nicht alle ChatGPT-Modelle"), new("Verlängerung / Rechnungen", "Von dieser Schnittstelle nicht geliefert") };
        string? email = Text(account, "email");
        if (email is not null) facts.Add(new("Konto", MaskEmail(email)));
        Field(facts, account, "planType", "Tarif");
        var quotas = new List<AiQuotaWindow>();
        var buckets = Get(limits, "rateLimitsByLimitId");
        void Bucket(JsonElement bucket, string id)
        {
            string name = Text(bucket, "limitName") ?? id;
            foreach (string key in new[] { "primary", "secondary" })
            {
                var w = Get(bucket, key);
                double? mins = Number(Get(w, "windowDurationMins"));
                string label = mins is null ? key : mins >= 1440 ? $"{mins / 1440:0.#}d" : mins >= 60 ? $"{mins / 60:0.#}h" : $"{mins:0}m";
                Window(quotas, w, name, label, "usedPercent", "resetsAt");
            }
            Field(facts, bucket, "planType", name + " · Tarif");
            Field(facts, bucket, "rateLimitReachedType", name + " · Limit erreicht");
            var credits = Get(bucket, "credits");
            Field(facts, credits, "balance", name + " · Guthaben");
            Field(facts, credits, "unlimited", name + " · Unbegrenzt");
            Field(facts, credits, "hasCredits", name + " · Guthaben vorhanden");
        }
        if (subscription)
        {
            if (buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any())
                foreach (var bucket in buckets.EnumerateObject().Take(20)) Bucket(bucket.Value, Clean(bucket.Name));
            else if (Get(limits, "rateLimits").ValueKind == JsonValueKind.Object) Bucket(Get(limits, "rateLimits"), "codex");
        }
        Field(facts, Get(limits, "rateLimitResetCredits"), "availableCount", "Verfügbare Limit-Resets");
        var summary = Get(usage, "summary");
        foreach (var (key, label) in new[] { ("lifetimeTokens", "Token-Aktivität gesamt"), ("peakDailyTokens", "Token-Tagesmaximum"), ("longestRunningTurnSec", "Längste Antwort (s)"), ("currentStreakDays", "Aktuelle Serie (Tage)"), ("longestStreakDays", "Längste Serie (Tage)") }) Field(facts, summary, key, label);
        var daily = Get(usage, "dailyUsageBuckets");
        if (daily.ValueKind == JsonValueKind.Array) foreach (var day in daily.EnumerateArray().TakeLast(31)) Field(facts, day, "tokens", "Token-Aktivität " + (Text(day, "startDate") ?? "Tag"));
        var messages = Get(notices, "messages");
        if (messages.ValueKind == JsonValueKind.Array) foreach (var m in messages.EnumerateArray().Take(10)) Field(facts, m, "headline", "Workspace-Hinweis");
        return new("Codex", Text(account, "planType") is { } plan ? "ChatGPT " + plan : type == "apiKey" ? "API-Key" : "",
            type == "none" ? "Nicht angemeldet" : type == "apiKey" ? "API-Abrechnung · keine Abo-Limits" : quotas.Count == 0 ? "Keine Limits geliefert" : "Kontoweite Nutzung", observed, quotas, facts);
    }

    public static AiAccountSnapshot Claude(JsonElement auth, IReadOnlyList<JsonElement> snapshots)
    {
        string method = Text(auth, "authMethod") ?? "unbekannt";
        bool loggedIn = Get(auth, "loggedIn").ValueKind == JsonValueKind.True;
        var facts = new List<AiFact> { new("Anmeldung", method), new("Quelle", "Claude auth status / Statuszeile"), new("Geltungsbereich", "Claude-Konto; Sitzungswerte separat"), new("Verlängerung / Rechnungen", "Von dieser Schnittstelle nicht geliefert") };
        if (Text(auth, "email") is { } email) facts.Add(new("Konto", MaskEmail(email)));
        foreach (var (key, label) in new[] { ("subscriptionType", "Tarif"), ("orgName", "Organisation"), ("apiProvider", "API-Anbieter") }) Field(facts, auth, key, label);
        var sorted = snapshots.Where(s => Epoch(Get(s, "observed_at")) is not null && new[] { "authMethod", "email", "orgId" }.All(key => Text(Get(s, "auth_identity"), key) == Text(auth, key)))
            .OrderByDescending(s => Epoch(Get(s, "observed_at"))).Take(20).ToArray();
        var latest = sorted.FirstOrDefault();
        var quotas = new List<AiQuotaWindow>();
        var rates = Get(latest, "rate_limits");
        Window(quotas, Get(rates, "five_hour"), "Claude", "5h", "used_percentage", "resets_at");
        Window(quotas, Get(rates, "seven_day"), "Claude", "7d", "used_percentage", "resets_at");
        Window(quotas, Get(rates, "spend_limit"), "Gateway-Budget", "Budget", "used_percentage", "resets_at");
        foreach (var snapshot in sorted)
        {
            string session = Text(snapshot, "session_id") ?? "Sitzung";
            facts.Add(new("Sitzung", session));
            Field(facts, Get(snapshot, "model"), "display_name", "Modell");
            Field(facts, Get(snapshot, "context_window"), "used_percentage", "Kontext belegt (%)");
            foreach (var (key, label) in new[] { ("total_input_tokens", "Input-Token"), ("total_output_tokens", "Output-Token") }) Field(facts, Get(snapshot, "context_window"), key, label);
            foreach (var (key, label) in new[] { ("total_cost_usd", "Sitzungsschätzung USD (keine Abo-Kosten)"), ("total_duration_ms", "Sitzungsdauer (ms)"), ("total_api_duration_ms", "API-Dauer (ms)") }) Field(facts, Get(snapshot, "cost"), key, label);
        }
        bool api = method is "api_key" or "api_key_helper" or "third_party";
        // A snapshot alone never proves that the current CLI login is still the same account.
        if (!loggedIn || api) quotas.Clear();
        return new("Claude Code", Text(auth, "subscriptionType") ?? (api ? "API" : loggedIn ? "Abo" : ""),
            !loggedIn ? "Nicht angemeldet" : api ? "API-Anmeldung · keine Abo-Limits" : sorted.Length == 0 ? "Statuszeile noch nicht eingerichtet" : quotas.Count == 0 ? "Warte auf Claude-Aktivität / Limits" : "Kontoweite Nutzung",
            Epoch(Get(latest, "observed_at")), quotas, facts) { NeedsCollector = loggedIn && !api && sorted.Length == 0 };
    }

    public static AiAccountSnapshot OpenRouter(JsonElement response, DateTimeOffset observed)
    {
        var data = Get(response, "data");
        var facts = new List<AiFact> { new("Quelle", "OpenRouter /api/v1/key"), new("Geltungsbereich", "Hinterlegter API-Key; kein Chat-Abonnement") };
        foreach (var (key, label) in new[] { ("label", "Bezeichnung"), ("limit", "Key-Limit USD"), ("limit_remaining", "Key-Restbudget USD"), ("limit_reset", "Budget-Zeitraum"), ("usage", "Verbrauch USD"), ("usage_daily", "Verbrauch heute USD"), ("usage_weekly", "Verbrauch Woche USD"), ("usage_monthly", "Verbrauch Monat USD"), ("is_free_tier", "Kostenloser Tarif") }) Field(facts, data, key, label);
        var quotas = new List<AiQuotaWindow>();
        double? limit = Number(Get(data, "limit")), remaining = Number(Get(data, "limit_remaining"));
        if (limit > 0 && remaining is not null) quotas.Add(new("Key-Budget", "USD", Math.Clamp((limit.Value - remaining.Value) / limit.Value * 100, 0, 100), null));
        if (Get(data, "budgets").ValueKind == JsonValueKind.Array) foreach (var b in Get(data, "budgets").EnumerateArray().Take(20))
            foreach (var key in new[] { "scope", "limit_usd", "spend_usd", "remaining_usd", "reset_interval", "resets_at" }) Field(facts, b, key, "Budget · " + key);
        return new("OpenRouter", "API", "Key-Verbrauch / Budget", observed, quotas, facts);
    }

    public static string MaskEmail(string email)
    {
        int at = email.IndexOf('@');
        return at > 0 ? email[..1] + "•••" + email[at..] : "Konto angemeldet";
    }
}
