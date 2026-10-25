using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <param name="Release">The latest release, or null when there is none or it could not be read.</param>
/// <param name="Error">Why not (a short German sentence for the status line), null when a release was found.</param>
public sealed record UpdateCheckResult(UpdateRelease? Release, string? Error);

/// <summary>Looks for a newer release on GitHub and prepares it: download, checksum, unpack. It never touches the installation.</summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _api;
    private readonly bool _allowLoopback;

    /// <param name="allowLoopback">Only for tests: downloads from 127.0.0.1 are allowed besides GitHub's own hosts.</param>
    public UpdateService(Uri? api = null, bool allowLoopback = false, HttpMessageHandler? handler = null, string appVersion = "0.0.0")
    {
        _api = api ?? UpdateInfo.LatestReleaseUrl;
        _allowLoopback = allowLoopback;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = Timeout.InfiniteTimeSpan; // limits come from cancellation tokens
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TerminalMgr", appVersion));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>TERMINALMGR_UPDATE_URL points the check at another API address (tests and development only).</summary>
    public static UpdateService FromEnvironment(string appVersion)
    {
        string? custom = Environment.GetEnvironmentVariable("TERMINALMGR_UPDATE_URL");
        return Uri.TryCreate(custom, UriKind.Absolute, out var api) ? new(api, allowLoopback: api.IsLoopback, appVersion: appVersion) : new(appVersion: appVersion);
    }

    public async Task<UpdateCheckResult> CheckAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _client.GetAsync(_api, limit.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(null, "Kein Release gefunden (Repository nicht öffentlich oder noch kein Release veröffentlicht).");
            if (!response.IsSuccessStatusCode) return new(null, $"Update-Prüfung nicht möglich (HTTP {(int)response.StatusCode}).");
            string json = await response.Content.ReadAsStringAsync(limit.Token);
            var release = UpdateInfo.ParseLatestRelease(json, url => UpdateInfo.IsAllowedDownload(url, _allowLoopback));
            return release is null ? new(null, "Das neueste Release enthält keine verwendbare Installationsdatei.") : new(release, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "Update-Prüfung nicht möglich (Zeitüberschreitung)."); }
        catch (HttpRequestException) { return new(null, "Update-Prüfung nicht möglich (keine Verbindung)."); }
    }

    /// <summary>
    /// Downloads the ZIP and its checksum, verifies the SHA-256, unpacks into <paramref name="updateRoot"/>\&lt;version&gt;\app and
    /// returns that folder. On any failure everything of this attempt is deleted and an exception is thrown.
    /// </summary>
    public async Task<string> StageAsync(UpdateRelease release, string updateRoot, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(updateRoot);
        string work = Path.Combine(root, release.Version.ToString());
        if (!AppPaths.IsInside(work, root)) throw new InvalidOperationException("Ungültiger Update-Ordner.");
        DeleteQuietly(work);
        Directory.CreateDirectory(work);
        try
        {
            string zip = Path.Combine(work, release.ZipName);
            await DownloadAsync(release.ZipUrl, zip, release.ZipSize, progress, cancellationToken);
            string checksumText = await DownloadTextAsync(release.ChecksumUrl, cancellationToken);
            string expected = UpdateInfo.ParseChecksum(checksumText, release.ZipName) ?? throw new InvalidDataException("Die Prüfsummen-Datei ist unlesbar.");
            string actual = UpdateInfo.ComputeSha256(zip);
            if (actual != expected) throw new InvalidDataException("Die Prüfsumme der heruntergeladenen Datei stimmt nicht. Die Installation bleibt unverändert.");

            string extract = Path.Combine(work, "extract");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract), cancellationToken);
            string app = Path.Combine(work, "app");
            // The release zip holds one folder (TerminalMgr-win-x64); a zip with the files at its root works as well.
            var folders = Directory.GetDirectories(extract);
            string source = File.Exists(Path.Combine(extract, "TerminalMgr.App.exe")) ? extract
                : folders.Length == 1 && File.Exists(Path.Combine(folders[0], "TerminalMgr.App.exe")) ? folders[0]
                : throw new InvalidDataException("Das Paket enthält keine TerminalMgr.App.exe.");
            UpdateSwap.MoveDirectory(source, app);
            File.Delete(zip);
            DeleteQuietly(extract);
            return app;
        }
        catch
        {
            DeleteQuietly(work);
            throw;
        }
    }

    /// <summary>Removes leftovers of earlier updates (staged folders, downloads); the log file stays.</summary>
    public static void CleanUp(string updateRoot)
    {
        if (!Directory.Exists(updateRoot)) return;
        foreach (string directory in Directory.GetDirectories(updateRoot)) DeleteQuietly(directory);
    }

    private async Task DownloadAsync(Uri url, string target, long expectedSize, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // GitHub redirects downloads to its storage host; wherever we end up must be an allowed host again.
        if (response.RequestMessage?.RequestUri is { } final && !UpdateInfo.IsAllowedDownload(final, _allowLoopback))
            throw new InvalidDataException("Der Download wurde auf eine nicht erlaubte Adresse umgeleitet.");
        long total = response.Content.Headers.ContentLength ?? expectedSize;
        long done = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                progress?.Report((done, total));
            }
        }
        if (total > 0 && done != total) throw new InvalidDataException($"Der Download ist unvollständig ({done} von {total} Bytes).");
    }

    private async Task<string> DownloadTextAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        return text.Length > 4096 ? throw new InvalidDataException("Die Prüfsummen-Datei ist unerwartet groß.") : text;
    }

    private static void DeleteQuietly(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _client.Dispose();
}
