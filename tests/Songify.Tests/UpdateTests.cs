using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;
using Songify.Core.Updates;

namespace Songify.Tests;

public class UpdateTests
{
    private static readonly AppVersion V030 = new(new Version(0, 3, 0));

    [Theory]
    [InlineData("0.3.0", "0.3.0", null)]
    [InlineData("v0.3.0", "0.3.0", null)]
    [InlineData("0.4.0-beta.1+abc123", "0.4.0", "beta.1")]
    [InlineData("1.2.3+5f1e2d", "1.2.3", null)]
    public void Versions_are_parsed_with_prerelease_and_build_metadata(string text, string core, string? prerelease) =>
        Assert.Equal(new AppVersion(Version.Parse(core), prerelease), AppUpdater.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    public void Unparseable_versions_are_null(string? text) => Assert.Null(AppUpdater.ParseVersion(text));

    [Fact]
    public void Development_builds_have_no_version_and_never_update()
    {
        Assert.Null(AppUpdater.CurrentVersion("0.0.0-dev+abc123"));
        Assert.Null(AppUpdater.CurrentVersion(null));
        Assert.Equal(V030, AppUpdater.CurrentVersion("0.3.0+abc123"));
        Assert.Equal("0.4.0-beta.1", AppUpdater.CurrentVersion("0.4.0-beta.1")!.ToString());
    }

    private static string Release(string tag, string asset = "Songify-win-x64.zip", string? page = null, string? url = null,
        string digest = "sha256:ABC123") => $$"""
        {"tag_name": "{{tag}}", "html_url": "{{page ?? $"https://github.com/Cothek/Songify/releases/tag/{tag}"}}",
         "assets": [
           {"name": "notes.txt", "browser_download_url": "https://github.com/Cothek/Songify/releases/download/{{tag}}/notes.txt", "size": 5},
           {"name": "{{asset}}", "browser_download_url": "{{url ?? $"https://github.com/Cothek/Songify/releases/download/{tag}/{asset}"}}",
            "size": 68701104, "digest": "{{digest}}"}
         ]}
        """;

    [Fact]
    public void A_failed_update_is_announced_once_and_forgotten_once_that_version_runs()
    {
        var failed = new FailedUpdate(new Version(0, 4, 0));

        Assert.Equal((failed with { Announced = true }, true), AppUpdater.ResolveFailedUpdate(V030, failed));
        Assert.Equal((failed with { Announced = true }, false), AppUpdater.ResolveFailedUpdate(V030, failed with { Announced = true }));
        Assert.Equal((null, false), AppUpdater.ResolveFailedUpdate(new AppVersion(new Version(0, 4, 0)), failed));
        Assert.Equal((null, false), AppUpdater.ResolveFailedUpdate(new AppVersion(new Version(0, 5, 0)), failed));
        Assert.Equal((null, false), AppUpdater.ResolveFailedUpdate(V030, null));
    }

    [Theory]
    [InlineData(new[] { "--updated-from", "1234" }, 1234)]
    [InlineData(new[] { "C:\\Songify.exe", "--updated-from", "1234" }, 1234)]
    [InlineData(new[] { "--updated-from" }, null)]
    [InlineData(new[] { "--updated-from", "x" }, null)]
    [InlineData(new[] { "--updated-from", "-5" }, null)]
    [InlineData(new string[0], null)]
    public void The_previous_process_is_read_from_the_command_line(string[] args, int? expected) =>
        Assert.Equal(expected, UpdateHandoff.PreviousProcessId(args));

    /// <summary>Runs a command; the handoff's own arguments land after "&amp; rem", so they're ignored.</summary>
    private static System.Diagnostics.ProcessStartInfo Cmd(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in new[] { "/c" }.Concat(command.Split(' ')).Concat(["&", "rem"])) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public void A_new_version_that_exits_without_confirming_failed() =>
        Assert.Equal(StartResult.Failed, UpdateHandoff.StartAndConfirm(Cmd("exit 3"), TimeSpan.FromSeconds(30)));

    [Fact]
    public void A_new_version_that_hangs_is_stopped()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(StartResult.Failed, UpdateHandoff.StartAndConfirm(Cmd("ping -n 5 127.0.0.1"), TimeSpan.FromSeconds(1)));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_new_version_that_confirms_started()
    {
        // The confirmation names this process, as the new exe's command line would.
        var confirm = Task.Run(async () =>
        {
            await Task.Delay(500);
            UpdateHandoff.ConfirmStarted(["--updated-from", Environment.ProcessId.ToString()]);
        });
        Assert.Equal(StartResult.Confirmed, UpdateHandoff.StartAndConfirm(Cmd("ping -n 3 127.0.0.1"), TimeSpan.FromSeconds(20)));
        await confirm;
    }

    [Fact]
    public void The_previous_process_is_opened_only_while_it_runs()
    {
        using var self = UpdateHandoff.OpenPrevious(["--updated-from", Environment.ProcessId.ToString()]);
        Assert.Equal(Environment.ProcessId, self?.Id);

        using var gone = System.Diagnostics.Process.Start(Cmd("exit 0"))!;
        gone.WaitForExit();
        Assert.Null(UpdateHandoff.OpenPrevious(["--updated-from", gone.Id.ToString()]));
        Assert.Null(UpdateHandoff.OpenPrevious([]));
    }

    [Fact]
    public void The_install_target_is_read_from_the_command_line()
    {
        Assert.Equal(@"C:\Apps\Songify.exe", UpdateHandoff.InstallTarget(["--install-into", @"C:\Apps\Songify.exe", "--updated-from", "12"]));
        Assert.Null(UpdateHandoff.InstallTarget(["--updated-from", "12"]));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\.net\Songify\abc", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\net\Songify\abc", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\.net\Other\abc", false)]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.0", false)]
    public void Extraction_folders_are_recognized(string folder, bool expected) =>
        Assert.Equal(expected, ExtractionCleanup.IsExtractionFolder(folder, "Songify"));

    [Fact]
    public void A_newer_release_with_the_download_is_offered()
    {
        var update = AppUpdater.ParseLatestRelease(Release("v0.4.0"), V030);

        Assert.Equal(new AvailableUpdate(new Version(0, 4, 0), "https://github.com/Cothek/Songify/releases/tag/v0.4.0",
            "https://github.com/Cothek/Songify/releases/download/v0.4.0/Songify-win-x64.zip", 68701104, "abc123"), update);
        Assert.Null(AppUpdater.ParseLatestRelease(Release("v0.4.0", digest: "md5:x"), V030)!.Sha256);
    }

    [Theory]
    [InlineData("v0.3.0")]        // same version
    [InlineData("v0.2.0")]        // older
    [InlineData("v0.5.0-beta.1")] // pre-release
    [InlineData("nightly")]       // not a version
    public void Other_releases_are_not_offered(string tag) =>
        Assert.Null(AppUpdater.ParseLatestRelease(Release(tag), V030));

    [Fact]
    public void A_beta_is_offered_the_stable_release_of_the_same_version()
    {
        var beta = new AppVersion(new Version(0, 4, 0), "beta.1");
        Assert.NotNull(AppUpdater.ParseLatestRelease(Release("v0.4.0"), beta));
        Assert.Null(AppUpdater.ParseLatestRelease(Release("v0.3.9"), beta));
    }

    [Theory]
    [InlineData("https://github.com/Cothek/Songify/releases/tag/v0.4.0", "https://evil.example/Songify-win-x64.zip")]
    [InlineData("https://github.com/Cothek/Songify/releases/tag/v0.4.0", "http://github.com/Cothek/Songify/releases/download/v0.4.0/Songify-win-x64.zip")]
    [InlineData("https://github.com/Cothek/Songify/releases/tag/v0.4.0", "https://github.com/someone/Songify/releases/download/v0.4.0/Songify-win-x64.zip")]
    [InlineData("file:///C:/Windows/System32/calc.exe", "https://github.com/Cothek/Songify/releases/download/v0.4.0/Songify-win-x64.zip")]
    [InlineData("https://github.com.evil.example/Cothek/Songify/releases/tag/v0.4.0", "https://github.com/Cothek/Songify/releases/download/v0.4.0/Songify-win-x64.zip")]
    public void Links_outside_this_repository_are_refused(string page, string url) =>
        Assert.Null(AppUpdater.ParseLatestRelease(Release("v0.4.0", page: page, url: url), V030));

    [Fact]
    public void A_release_without_the_download_or_odd_json_is_not_offered()
    {
        Assert.Null(AppUpdater.ParseLatestRelease(Release("v0.4.0", asset: "Songify-arm64.zip"), V030));
        Assert.Null(AppUpdater.ParseLatestRelease("[]", V030));
    }
}

public sealed class UpdateFileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("songify-update-");
    private string Exe => Path.Combine(_dir.FullName, "Songify.exe");

    public void Dispose() => _dir.Delete(recursive: true);

    private void Staged(string oldContent = "old", string newContent = "new")
    {
        File.WriteAllText(Exe, oldContent);
        var downloaded = Path.Combine(_dir.FullName, "download.exe");
        File.WriteAllText(downloaded, newContent);
        AppUpdater.Stage(downloaded, Exe);
    }

    [Fact]
    public void Commit_puts_the_new_exe_in_place_and_keeps_the_old_one_aside()
    {
        Staged();
        File.WriteAllText(Exe + ".old", "from an earlier update");

        AppUpdater.Commit(Exe);

        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(Exe + ".old"));
        Assert.False(File.Exists(Exe + ".new"));
    }

    [Fact]
    public void Install_copies_the_download_in_and_keeps_the_old_exe()
    {
        File.WriteAllText(Exe, "old");
        var downloaded = Path.Combine(_dir.FullName, "update", "Songify.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        File.WriteAllText(downloaded, "new");

        AppUpdater.Install(downloaded, Exe);

        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(Exe + ".old"));
        Assert.True(File.Exists(downloaded)); // the installer runs from it
    }

    [Fact]
    public void A_failed_swap_puts_the_old_exe_back()
    {
        Staged();

        using (new FileStream(Exe + ".new", FileMode.Open, FileAccess.Read, FileShare.None)) // e.g. antivirus scanning it
            Assert.ThrowsAny<IOException>(() => AppUpdater.Commit(Exe));

        Assert.Equal("old", File.ReadAllText(Exe));
        Assert.False(File.Exists(Exe + ".old"));
    }

    [Fact]
    public async Task Cleanup_removes_what_an_update_left()
    {
        File.WriteAllText(Exe, "new");
        File.WriteAllText(Exe + ".old", "old");
        File.WriteAllText(Exe + ".new", "partial");
        File.WriteAllText(Exe + ".failed", "didn't start");
        var local = Directory.CreateDirectory(Path.Combine(_dir.FullName, "local")).FullName;
        Directory.CreateDirectory(Path.Combine(local, "update"));

        await new AppUpdater(new HttpClient(), local).CleanUpAsync(Exe);

        Assert.Equal(["Songify.exe"], Directory.GetFiles(_dir.FullName).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(local, "update")));
    }

    [Fact]
    public void A_rollback_puts_the_old_exe_back_and_remembers_the_version_that_failed()
    {
        Staged();
        AppUpdater.Commit(Exe);
        var version = new Version(0, 4, 0);

        AppUpdater.RollBack(Exe, version);

        Assert.Equal("old", File.ReadAllText(Exe));
        Assert.Equal("new", File.ReadAllText(Exe + ".failed"));
        Assert.False(File.Exists(Exe + ".old"));
        Assert.Equal(new FailedUpdate(version), AppUpdater.ReadFailedUpdate(Exe));

        AppUpdater.SaveFailedUpdate(Exe, null);
        Assert.Null(AppUpdater.ReadFailedUpdate(Exe));
    }

    [Fact]
    public void A_rollback_without_the_old_exe_changes_nothing()
    {
        File.WriteAllText(Exe, "new");
        var version = new Version(0, 4, 0);

        Assert.Throws<FileNotFoundException>(() => AppUpdater.RollBack(Exe, version));

        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Null(AppUpdater.ReadFailedUpdate(Exe));
    }

    [Fact]
    public void A_rollback_that_cant_restore_the_old_exe_leaves_the_new_one_in_place()
    {
        Staged();
        AppUpdater.Commit(Exe);
        var version = new Version(0, 4, 0);

        using (new FileStream(Exe + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => AppUpdater.RollBack(Exe, version));

        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(Exe + ".old"));
        Assert.Contains("rename Songify.exe.old to Songify.exe", AppUpdater.ManualRestoreHint(Exe));
    }

    // The installer step, with the installed exe's start faked: these "exes" are text files.
    private string Mutex => $@"Local\songify-test-{Guid.NewGuid():N}";

    private string Downloaded()
    {
        var path = Path.Combine(_dir.FullName, "update", "Songify.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "new");
        return path;
    }

    private string? Install(StartResult installedStarts, string? mutex = null, string? self = null)
    {
        File.WriteAllText(Exe, "old");
        return UpdateHandoff.Install(["--install-into", Exe], new AppVersion(new Version(0, 4, 0)), mutex ?? Mutex,
            self ?? Downloaded(), _ => installedStarts);
    }

    [Fact]
    public void The_installer_puts_itself_in_place()
    {
        Assert.Null(Install(StartResult.Confirmed));
        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(Exe + ".old")); // deleted by the installed version once the installer exits
    }

    [Fact]
    public void The_installer_goes_back_when_the_new_version_doesnt_start()
    {
        var message = Install(StartResult.Failed);

        Assert.Equal("old", File.ReadAllText(Exe));
        Assert.Equal(new FailedUpdate(new Version(0, 4, 0)), AppUpdater.ReadFailedUpdate(Exe));
        Assert.Contains("went back to the previous version", message); // a text file can't be started again
    }

    [Fact]
    public void The_installer_doesnt_touch_a_version_it_couldnt_stop()
    {
        Assert.Contains("couldn't be stopped", Install(StartResult.StillRunning));
        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.True(File.Exists(Exe + ".old"));
    }

    [Fact]
    public void The_installer_changes_nothing_while_songify_runs()
    {
        var name = Mutex;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var running = new Mutex(true, name); // another Songify, started during the update
            held.Set();
            release.Wait();
            running.ReleaseMutex();
        });
        owner.Start();
        held.Wait();

        Assert.Contains("started again", Install(StartResult.Confirmed, mutex: name));
        Assert.Equal("old", File.ReadAllText(Exe));
        release.Set();
        owner.Join();
    }

    [Fact]
    public void The_lock_is_free_while_someone_only_has_it_open()
    {
        var name = Mutex;
        using var openButNotHeld = new Mutex(false, name); // as the installer had while starting the new version
        var taken = InstanceLock.TryAcquire(name);
        Assert.NotNull(taken);
        InstanceLock.Release(taken!);
    }

    [Fact]
    public void A_lock_left_by_a_crashed_songify_can_be_taken()
    {
        var name = Mutex;
        var crashed = new Thread(() => new Mutex(true, name)); // ends while holding it: the mutex is abandoned
        crashed.Start();
        crashed.Join();

        var taken = InstanceLock.TryAcquire(name);
        Assert.NotNull(taken);
        InstanceLock.Release(taken!);
    }

    [Fact]
    public void The_installer_keeps_the_old_version_if_it_cant_replace_it()
    {
        var downloaded = Downloaded();
        File.WriteAllText(Exe, "old");
        string? message;
        using (new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.None)) // e.g. antivirus scanning it
            message = UpdateHandoff.Install(["--install-into", Exe], new AppVersion(new Version(0, 4, 0)), Mutex, downloaded,
                _ => throw new InvalidOperationException("mustn't start"));

        Assert.StartsWith("Couldn't update to Songify 0.4.0", message);
        Assert.Equal("old", File.ReadAllText(Exe));
    }

    [Fact]
    public void The_installer_never_replaces_itself()
    {
        Assert.Null(Install(StartResult.Confirmed, self: Exe));
        Assert.Equal("old", File.ReadAllText(Exe));
    }

    [Fact]
    public void Only_unused_old_extraction_folders_are_stale()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir.FullName, ".net", "Songify")).FullName;
        var old = DateTime.UtcNow.AddDays(-3);
        string Folder(string name, DateTime created)
        {
            var path = Directory.CreateDirectory(Path.Combine(app, name)).FullName;
            Directory.SetCreationTimeUtc(path, created);
            return path;
        }
        var own = Folder("own", DateTime.UtcNow);
        Folder("used-by-another-songify", old);
        Folder("just-unpacked", DateTime.UtcNow.AddMinutes(-1));
        var stale = Folder("earlier-version", old);

        var result = ExtractionCleanup.StaleFolders(own, new HashSet<string> { "own", "used-by-another-songify" }, DateTime.UtcNow);

        Assert.Equal([stale], result);
    }

    [Fact]
    public void Running_from_a_temp_folder_is_refused_but_a_normal_folder_is_fine()
    {
        Assert.Contains("temporary folder", AppUpdater.WhyCantReplace(Exe)); // the test folder is under %TEMP%
        Assert.Null(AppUpdater.WhyCantReplace(Path.Combine(AppContext.BaseDirectory, "Songify.exe")));
    }

    /// <summary>Serves one response body for any request.</summary>
    private sealed class FixedResponse(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] Zip(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(entryName).Open();
            entry.Write(content);
        }
        return ms.ToArray();
    }

    private Task<string> DownloadAsync(byte[] zip, long? size = null, string? sha256 = null)
    {
        var update = new AvailableUpdate(new Version(0, 4, 0), "https://github.com/Cothek/Songify/releases/tag/v0.4.0",
            "https://github.com/Cothek/Songify/releases/download/v0.4.0/Songify-win-x64.zip", size ?? zip.Length, sha256);
        return new AppUpdater(new HttpClient(new FixedResponse(zip)), _dir.FullName).DownloadAsync(update, null, default);
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_is_refused()
    {
        var zip = Zip("Songify.exe", "MZ"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(zip, size: zip.Length - 1)); // too long
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(zip, size: zip.Length + 1)); // incomplete
    }

    [Fact]
    public async Task A_download_with_a_wrong_checksum_is_refused()
    {
        var zip = Zip("Songify.exe", "MZ"u8.ToArray());
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(zip, sha256: new string('0', 64)));
        Assert.Contains("checksum", ex.Message);
    }

    [Fact]
    public async Task A_download_without_the_exe_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(Zip("readme.txt", "hi"u8.ToArray())));
        Assert.Contains("doesn't contain Songify.exe", ex.Message);
    }

    [Fact]
    public async Task An_exe_that_isnt_the_release_version_is_refused()
    {
        var zip = Zip("Songify.exe", "not a real exe"u8.ToArray());
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DownloadAsync(zip, sha256: Convert.ToHexStringLower(SHA256.HashData(zip)))); // checksum passes
        Assert.Contains("version unknown, not 0.4.0", ex.Message);
    }
}

public sealed class UpdateHoldTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("songify-hold-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Waits_until_no_song_is_being_saved()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "data"), Path.Combine(_dir.FullName, "local"));
        Directory.CreateDirectory(paths.DataDir);
        JsonFile.Save(paths.LibraryFile, new List<TrackRecord>
        {
            new() { VideoId = "a", OriginalTitle = "a", Status = TrackStatus.Done },
            new() { VideoId = "b", OriginalTitle = "b" },
        });
        await using var service = new SongifyService(paths);
        var busy = service.GetTracks().Single(t => t.VideoId == "b");
        busy.Status = TrackStatus.Downloading;
        var reported = new List<int>();

        var wait = service.HoldAndWaitUntilIdleAsync(new Progress<int>(n => { lock (reported) reported.Add(n); }), default);
        await Task.Delay(700);
        Assert.False(wait.IsCompleted);

        busy.Status = TrackStatus.WaitingToRecord; // waiting for the PC to be idle doesn't hold up an update
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        lock (reported) Assert.Contains(1, reported);
        service.ResumeWork();
    }
}
