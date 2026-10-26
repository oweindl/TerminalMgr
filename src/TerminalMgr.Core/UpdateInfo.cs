using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalMgr.Core;

/// <param name="Notes">Release notes (the body of the GitHub release).</param>
public sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri ZipUrl, Uri ChecksumUrl, string ZipName, long ZipSize);

/// <summary>Everything about "is there a newer release and may I trust its files" that needs no network and no UI.</summary>
public static partial class UpdateInfo
{
    public const string Repository = "oweindl/TerminalMgr";
    public static Uri LatestReleaseUrl => new($"https://api.github.com/repos/{Repository}/releases/latest");

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:\+.*)?$")]
    private static partial Regex VersionText();

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = VersionText().Match(text?.Trim() ?? "");
        if (!match.Success) return false;
        version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return true;
    }

    /// <summary>The processor architectures a release holds a package for.</summary>
    public static readonly string[] Architectures = ["x64", "arm64", "x86"];

    /// <summary>The architecture of this running program ("x64", "arm64" or "x86"). A program started under emulation is updated with its own kind of package.</summary>
    public static string CurrentArchitecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64"
    };

    /// <summary>"TerminalMgr-win-x64-0.2.34.zip": one package per architecture (older releases only have x64).</summary>
    public static string FileName(Version version, string? architecture = null) => $"TerminalMgr-win-{architecture ?? CurrentArchitecture}-{version}.zip";

    /// <summary>
    /// The release from GitHub's "latest release" JSON, or null when it is not usable: draft, pre-release, no version tag,
    /// or the ZIP and its .sha256 are not both attached under a download address that passes <paramref name="isAllowedUrl"/>.
    /// </summary>
    public static UpdateRelease? ParseLatestRelease(string json, Func<Uri, bool> isAllowedUrl, string? architecture = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var version)) return null;
            string zipName = FileName(version, architecture);
            Uri? zip = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!asset.TryGetProperty("browser_download_url", out var u) || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url) || !isAllowedUrl(url)) continue;
                    if (name == zipName) { zip = url; size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long l) ? l : 0; }
                    else if (name == zipName + ".sha256") sha = url;
                }
            if (zip is null || sha is null) return null;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new(version, tag, notes, zip, sha, zipName, size);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// When the latest release is fine but holds no package for <paramref name="architecture"/>: a message that says so (and which packages exist);
    /// null when the release is unusable for other reasons or does have the package.
    /// </summary>
    public static string? MissingArchitectureMessage(string json, string? architecture = null)
    {
        architecture ??= CurrentArchitecture;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var names = assets.EnumerateArray().Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList();
            if (names.Any(n => n.StartsWith($"TerminalMgr-win-{architecture}-", StringComparison.Ordinal) && n.EndsWith(".zip", StringComparison.Ordinal))) return null;
            var others = Architectures.Where(a => a != architecture && names.Any(n => n.StartsWith($"TerminalMgr-win-{a}-", StringComparison.Ordinal))).ToList();
            return others.Count == 0 ? null : $"Das neueste Release hat kein Paket für Ihren Prozessor ({architecture}); vorhanden: {string.Join(", ", others)}.";
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Only GitHub's own download hosts over https; loopback addresses only for tests (<paramref name="allowLoopback"/>).</summary>
    public static bool IsAllowedDownload(Uri url, bool allowLoopback = false)
    {
        if (allowLoopback && url.IsLoopback && url.Scheme is "http" or "https") return true;
        if (url.Scheme != "https") return false;
        string host = url.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".githubusercontent.com");
    }

    public static bool IsNewer(Version current, Version candidate) => candidate > current;

    /// <summary>A release is offered when it is newer than the running version and was not skipped by the user.</summary>
    public static bool ShouldOffer(Version current, UpdateRelease release, string? skippedVersion) =>
        IsNewer(current, release.Version) &&
        !(TryParseVersion(skippedVersion, out var skipped) && skipped == release.Version);

    /// <summary>The hash for <paramref name="fileName"/> from a "hash  name" (sha256sum) file; a file with only a hash also works.</summary>
    public static string? ParseChecksum(string text, string fileName)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 64) continue;
            string hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            string rest = line[64..].Trim().TrimStart('*').Trim();
            if (rest.Length == 0 || string.Equals(rest, fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return null;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Self-update only for the installed copy; a development build or a copy elsewhere is never replaced.</summary>
    public static bool IsInstalledCopy(string? processPath, string installDirectory) =>
        !string.IsNullOrEmpty(processPath) && AppPaths.IsInside(processPath, installDirectory) &&
        string.Equals(Path.GetFileName(processPath), "TerminalMgr.App.exe", StringComparison.OrdinalIgnoreCase);
}
