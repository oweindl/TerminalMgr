using System.Text.Json;
using TerminalMgr.Core;
using TerminalMgr.Windows;

internal static class AiAccountTests
{
    public static void Run(Action<bool, string> check)
    {
        static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        var now = DateTimeOffset.UtcNow;
        var account = Json("""{"account":{"type":"chatgpt","email":"someone@example.test","planType":"pro","accessToken":"secret-not-for-display"}}""");
        var limits = Json("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":23,"windowDurationMins":300,"resetsAt":1999999999},"secondary":{"usedPercent":41,"windowDurationMins":10080}},"other":{"primary":{"usedPercent":12,"windowDurationMins":60}}},"rateLimitResetCredits":{"availableCount":2}}""");
        var parsed = AiAccountParser.Codex(account, limits, default, default, now);
        check(parsed.Quotas.Count == 3 && parsed.Quotas[0].UsedPercent == 23 && parsed.Quotas[0].Label == "5h", "Multi-bucket quotas must supersede the legacy bucket");
        check(parsed.Quotas[1].Label == "7d" && parsed.Quotas[1].ResetsAt is null, "Missing reset must stay unknown");
        string serialized = JsonSerializer.Serialize(parsed);
        check(!serialized.Contains("secret-not-for-display") && !serialized.Contains("someone@"), "Raw identity/token leaked into display model");
        var api = AiAccountParser.Codex(Json("""{"account":{"type":"apiKey"}}"""), limits, default, default, now);
        check(api.Quotas.Count == 0 && api.Plan == "API-Key", "API login must not show subscription limits");
        var missing = AiAccountParser.Codex(account, default, default, default, now);
        check(missing.Quotas.Count == 0, "Missing data must not become 0%");
        var auth = Json("""{"loggedIn":true,"authMethod":"claude.ai","email":"one@example.test"}""");
        var snapshot = Json("""{"observed_at":1893456000,"session_id":"session-one","auth_identity":{"authMethod":"claude.ai","email":"one@example.test"},"rate_limits":{"five_hour":{"used_percentage":38,"resets_at":1999999999},"seven_day":{"used_percentage":null}},"context_window":{"used_percentage":31},"cost":{"total_cost_usd":0.42},"transcript":"PRIVATE","access_token":"SECRET"}""");
        var claude = AiAccountParser.Claude(auth, [snapshot]);
        check(claude.Quotas.Count == 1 && claude.Quotas[0].UsedPercent == 38 && !claude.NeedsCollector, "Independent missing Claude windows");
        check(!JsonSerializer.Serialize(claude).Contains("PRIVATE") && !JsonSerializer.Serialize(claude).Contains("SECRET"), "Claude allowlist failed");
        var other = AiAccountParser.Claude(Json("""{"loggedIn":true,"authMethod":"claude.ai","email":"two@example.test"}"""), [snapshot]);
        check(other.Quotas.Count == 0 && other.NeedsCollector, "Previous login's snapshot must not become current account's usage");
        check(AiAccountParser.Claude(Json("""{"loggedIn":false,"authMethod":"none"}"""), [snapshot]).Quotas.Count == 0, "Signed-out account showed old quotas");
        var expired = new AiQuotaWindow("x", "5h", 90, now.AddSeconds(-1));
        check(expired.Expired(now) && expired.ResetText(now) == "Update ausstehend", "Expired window must await observation, never reset to 0%");
        check((parsed with { ObservedAt = now.AddMinutes(-6) }).IsStale(now), "Stale readings must be visible");
        var router = AiAccountParser.OpenRouter(Json("""{"data":{"limit":100,"limit_remaining":75,"usage":25,"api_key":"SECRET"}}"""), now);
        check(router.Quotas.Single().UsedPercent == 25 && !JsonSerializer.Serialize(router).Contains("SECRET"), "OpenRouter budget or secret handling");
        var unlimited = AiAccountParser.OpenRouter(Json("""{"data":{"limit":null,"limit_remaining":null}}"""), now);
        check(unlimited.Quotas.Count == 0, "Unlimited/missing budget must not become 0%");
        var profile = new Profile { Name = "example", Destination = "dev", ConfigPath = "C:/my config", Mode = StartMode.Tmux };
        var ssh = AiAccountSource.Command(profile, "codex", "app-server");
        check(ssh.ArgumentList.Contains("-T") && ssh.ArgumentList.Contains("RemoteCommand=none") && ssh.ArgumentList.Contains("BatchMode=yes") && ssh.ArgumentList.Contains(profile.ConfigPath), "Account query must override tmux and preserve SSH config");
        check(!ssh.ArgumentList.Contains("-tt") && ssh.RedirectStandardInput && ssh.RedirectStandardOutput, "Account query must not use PTY");
        Console.WriteLine("PASS AI accounts: multiple buckets, API/unknown states, account isolation, expiry, safe metadata, SSH separation");
    }
}
