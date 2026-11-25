using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Songify.Core.Infrastructure;
using static Songify.Core.Infrastructure.JsonValues;

namespace Songify.Core.Updates;

/// <summary>A release version: "0.4.0", or "0.4.0-beta.1" with a pre-release label.</summary>
public sealed record AppVersion(Version Core, string? Prerelease = null)
{
    /// <summary>True if a stable <paramref name="release"/> should replace this version.</summary>
    public bool IsOlderThan(Version release) => release > Core || (release == Core && Prerelease is not null);

    public override string ToString() => Prerelease is null ? Core.ToString() : $"{Core}-{Prerelease}";
}

/// <summary>A newer release on GitHub.</summary>
/// <param name="Sha256">The zip's SHA-256 as GitHub reports it (hex), when it does.</param>
public sealed record AvailableUpdate(Version Version, string PageUrl, string ZipUrl, long ZipSize, string? Sha256 = null);

/// <summary>An update that didn't start, so Songify went back to the version it had.</summary>
/// <param name="Announced">The user has been told.</param>
public sealed record FailedUpdate(Version Version, bool Announced = false);

/// <summary>
/// One-click updates from GitHub releases. The download is a zip holding Songify.exe, unpacked to the update folder.
/// Once the old version has exited, the new one installs itself from there (<see cref="UpdateHandoff"/>), keeping the
/// old exe as Songify.exe.old until the new version has started. Settings, the library and tools live in AppData and
/// are untouched.
/// </summary>
public sealed class AppUpdater(HttpClient http, string localDir)
{
    private const string Repository = "Cothek/Songify";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    private const string LatestReleaseUrl =$"https://api.github.com/repos/{Repository}/releases/latest";
    private const string AssetName = "Songify-win-x64.zip";
    private const string ExeName = "Songify.exe";
    /// <summary>Next to Songify.exe during an update: the previous exe, the new one being put in place, and one that didn't start.</summary>
    private const string OldSuffix = ".old", NewSuffix = ".new", FailedSuffix = ".failed";
    private const int RenameAttempts = 5;
    private static readonly TimeSpan RenameRetryDelay = TimeSpan.FromMilliseconds(200);

    private string UpdateDir => Path.Combine(localDir, "update");

    /// <summary>
    /// The release version this build was made from, or null for builds that shouldn't update themselves
    /// (local and CI builds carry the version "0.0.0-dev").
    /// </summary>
    public static AppVersion? CurrentVersion(string? informationalVersion) =>
        ParseVersion(informationalVersion) is { Prerelease: not "dev" } version ? version : null;

    /// <summary><see cref="CurrentVersion(string?)"/> of the running app.</summary>
    public static AppVersion? CurrentVersion() => CurrentVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>"0.3.0", "v0.3.0", "0.4.0-beta.1+abc123" → 0.3.0, 0.4.0-beta.1. Null if unparseable.</summary>
    public static AppVersion? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus]; // build metadata, e.g. the commit hash .NET appends
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        if (!Version.TryParse(core, out var version) || core.Count(c => c == '.') != 2) return null;
        return new AppVersion(version, dash >= 0 ? s[(dash + 1)..] : null);
    }

    /// <summary>
    /// The latest stable release, if it's newer than <paramref name="current"/> and has the download. Its links must
    /// point at this repository on github.com: the page is opened with the shell, and the zip becomes the app.
    /// </summary>
    public static AvailableUpdate? ParseLatestRelease(string json, AppVersion current)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        if (ParseVersion(Text(root, "tag_name")) is not { Prerelease: null, Core: var version } || !current.IsOlderThan(version))
            return null;
        var page = Text(root, "html_url");
        var asset = (root["assets"] as JsonArray ?? []).FirstOrDefault(a => Text(a, "name") == AssetName);
        var url = Text(asset, "browser_download_url");
        var size = Number(asset, "size");
        if (!IsRepositoryUrl(page, "releases/tag/") || !IsRepositoryUrl(url, "releases/download/") || size is not > 0)
            return null;
        var digest = Text(asset, "digest");
        var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..].ToLowerInvariant() : null;
        return new AvailableUpdate(version, page!, url!, (long)size, sha256);
    }

    private static bool IsRepositoryUrl(string? url, string pathPrefix) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort
        && uri.AbsolutePath.StartsWith($"/{Repository}/{pathPrefix}", StringComparison.Ordinal);

    /// <summary>Asks GitHub for the latest release. Null when up to date; network problems are logged, not thrown.</summary>
    public async Task<AvailableUpdate?> CheckAsync(AppVersion current, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"Update check failed: GitHub answered {(int)response.StatusCode}");
                return null;
            }
            var update = ParseLatestRelease(await response.Content.ReadAsStringAsync(ct), current);
            if (update is not null) AppLog.Info($"Songify {update.Version} is available");
            return update;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            AppLog.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the release zip, checks it, and unpacks Songify.exe into the update folder. Returns its path.
    /// <paramref name="progress"/> gets 0..1 from the downloading thread.
    /// </summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, Action<double>? progress, CancellationToken ct)
    {
        if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true);
        Directory.CreateDirectory(UpdateDir);
        var zip = Path.Combine(UpdateDir, AssetName);
        await HttpDownload.ToFileAsync(http, update.ZipUrl, zip, (done, _) => progress?.Invoke((double)done / update.ZipSize), ct,
            maxBytes: update.ZipSize);

        return await Task.Run(() => Unpack(update, zip), ct); // ~70 MB of hashing and unzipping
    }

    private string Unpack(AvailableUpdate update, string zip)
    {
        if (new FileInfo(zip).Length != update.ZipSize)
            throw new InvalidDataException("The download was incomplete");
        if (update.Sha256 is { } expected)
        {
            using var stream = File.OpenRead(zip);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != expected)
                throw new InvalidDataException("The download is damaged (its checksum doesn't match)");
        }

        var exe = Path.Combine(UpdateDir, ExeName);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"The download doesn't contain {ExeName}");
            entry.ExtractToFile(exe);
        }
        File.Delete(zip);

        // Guards against a mislabeled or wrong asset: the exe must be the version the release says.
        var fileVersion = ParseVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
        if (fileVersion?.Core != update.Version || fileVersion.Prerelease is not null)
            throw new InvalidDataException($"The downloaded {ExeName} is version {fileVersion?.ToString() ?? "unknown"}, not {update.Version}");
        return exe;
    }

    /// <summary>
    /// Why Songify can't replace itself where it runs from, or null if it can. Not in a read-only folder (e.g. Program
    /// Files without admin rights), and not in a temp folder (e.g. opened straight from the zip), which gets deleted.
    /// </summary>
    public static string? WhyCantReplace(string currentExe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(currentExe))!;
        if ((folder + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            return "Songify is running from a temporary folder (opened from the zip?). Unzip it to a folder first, then update";
        var probe = Path.Combine(folder, $"~songify-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Songify can't replace itself in {folder}. Move Songify.exe to a folder you can write to, such as your Documents";
        }
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in place of <paramref name="currentExe"/>, keeping the old one as .old. Only once
    /// the old version has exited: a single-file exe reads parts of itself from its path while it runs, so a running
    /// Songify.exe must never be renamed or replaced.
    /// </summary>
    public static void Install(string newExe, string currentExe)
    {
        Stage(newExe, currentExe);
        Commit(currentExe);
    }

    /// <summary>Copies the new exe next to the current one, as .new.</summary>
    public static void Stage(string newExe, string currentExe) => File.Copy(newExe, currentExe + NewSuffix, overwrite: true);

    /// <summary>
    /// Swaps the staged exe in: two renames on one drive. Each is retried (antivirus often scans a fresh exe, and the
    /// old process may be letting go of its exe); if the second still fails, the first is undone.
    /// </summary>
    public static void Commit(string currentExe)
    {
        var staged = currentExe + NewSuffix;
        var old = currentExe + OldSuffix;
        if (!File.Exists(staged)) throw new FileNotFoundException("The new version wasn't prepared", staged);
        if (File.Exists(old)) File.Delete(old); // left from the last update if its cleanup failed
        MoveWithRetries(currentExe, old);
        try
        {
            MoveWithRetries(staged, currentExe);
        }
        catch (Exception swapError)
        {
            try
            {
                MoveWithRetries(old, currentExe);
            }
            catch (Exception undoError)
            {
                throw new InvalidOperationException(
                    $"The update failed and Songify couldn't undo it. {ManualRestoreHint(currentExe)}",
                    new AggregateException(swapError, undoError));
            }
            throw;
        }
    }

    /// <summary>A rename retried briefly: antivirus scanning a fresh exe, or a process that is still exiting, holds it.</summary>
    private static void MoveWithRetries(string from, string to, bool overwrite = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < RenameAttempts)
            {
                Thread.Sleep(RenameRetryDelay);
            }
        }
    }

    /// <summary>
    /// Undoes <see cref="Install"/> after the new version failed to start: the new exe goes aside as .failed (deleted on
    /// the next start), the old one comes back, and the failure is noted next to the exe so the version isn't offered again.
    /// </summary>
    /// <exception cref="FileNotFoundException">The old exe is gone; nothing was changed.</exception>
    /// <exception cref="IOException">The swap failed and was undone: the new exe is in place and the old one is .old.</exception>
    /// <param name="failedVersion">Null for a development build: nothing is noted.</param>
    public static void RollBack(string currentExe, Version? failedVersion)
    {
        var old = currentExe + OldSuffix;
        var failed = currentExe + FailedSuffix;
        if (!File.Exists(old)) throw new FileNotFoundException("The previous version is gone", old);
        MoveWithRetries(currentExe, failed, overwrite: true);
        try
        {
            MoveWithRetries(old, currentExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoveWithRetries(failed, currentExe); // as before the rollback, which the hint describes
            throw new IOException(ex.Message, ex);
        }
        if (failedVersion is not null) SaveFailedUpdate(currentExe, new FailedUpdate(failedVersion));
    }

    /// <summary>A release's page; tags are "v" + the version.</summary>
    public static string ReleasePage(Version version) => $"{ReleasesPage}/tag/v{version}";

    /// <summary>How to put the previous version back by hand.</summary>
    public static string ManualRestoreHint(string currentExe)
    {
        var name = Path.GetFileName(currentExe);
        return $"In {Path.GetDirectoryName(currentExe)}, delete {name} if it's there and rename {name}{OldSuffix} to {name}";
    }

    /// <summary>
    /// What to do with the note of a failed update when <paramref name="current"/> starts: drop it once that version (or a
    /// newer one) runs; otherwise keep it, so that version isn't offered again, and announce it the first time.
    /// </summary>
    public static (FailedUpdate? Keep, bool Announce) ResolveFailedUpdate(AppVersion current, FailedUpdate? failed) =>
        failed is null || !current.IsOlderThan(failed.Version) ? (null, false) : (failed with { Announced = true }, !failed.Announced);

    /// <summary>Kept next to the exe, not in AppData: it's about this copy of Songify.</summary>
    private static string FailedUpdateFile(string currentExe) =>
        Path.Combine(Path.GetDirectoryName(currentExe)!, Path.GetFileNameWithoutExtension(currentExe) + ".update-failed.json");

    /// <summary>The update that last failed to start here, if any.</summary>
    public static FailedUpdate? ReadFailedUpdate(string currentExe) => JsonFile.Load<FailedUpdate>(FailedUpdateFile(currentExe));

    /// <summary>Updates the note (null deletes it, e.g. once a newer version runs).</summary>
    public static void SaveFailedUpdate(string currentExe, FailedUpdate? failed)
    {
        var path = FailedUpdateFile(currentExe);
        try
        {
            if (failed is null) File.Delete(path);
            else JsonFile.Save(path, failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Couldn't update {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes what the last update left: the old exe and the download. Call it once the old process has exited
    /// (<see cref="UpdateHandoff.OpenPrevious"/>): until then its exe is the way back.
    /// </summary>
    public async Task CleanUpAsync(string currentExe)
    {
        foreach (var path in new[] { OldSuffix, NewSuffix, FailedSuffix }.Select(suffix => currentExe + suffix))
        {
            // The previous process may still be exiting for a moment.
            for (var attempt = 1; File.Exists(path); attempt++)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 20)
                    {
                        AppLog.Warn($"Couldn't delete {path} (it's deleted on the next start): {ex.Message}");
                        break;
                    }
                    await Task.Delay(500);
                }
            }
        }
        try
        {
            if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Couldn't delete {UpdateDir} (it's deleted on the next start): {ex.Message}");
        }
    }
}
