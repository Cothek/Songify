using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Songify.Core.Infrastructure;
using Songify.Core.Processing;
using Songify.Core.Updates;

namespace Songify.App;

/// <summary><see cref="UpdateStage.RolledBack"/>: the last update didn't start, so Songify went back to this version.</summary>
public enum UpdateStage { None, Available, Downloading, Waiting, Restarting, Failed, RolledBack }

/// <summary>
/// The update bar: checks GitHub for a newer release and, when the user asks, downloads it, waits until no song
/// is being saved, and exits for the new version to install itself. Never installs without the user's click.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan EditorPoll = TimeSpan.FromSeconds(1);

    private readonly SongifyService _service;
    private readonly Action _exitForUpdate;
    private readonly DispatcherTimer _timer;
    private readonly string? _exe = Environment.ProcessPath;
    /// <summary>Removes what the last update left; runs first so it can't delete a new download.</summary>
    private readonly Task _cleanup;
    /// <summary>The last update that didn't start here; it isn't offered again.</summary>
    private readonly FailedUpdate? _failed;
    private CancellationTokenSource? _installing;

    /// <param name="exitForUpdate">Closes Songify once the downloaded update runs and waits to install itself (<see cref="UpdateHandoff"/>).</param>
    public UpdateViewModel(SongifyService service, Action exitForUpdate, Dispatcher dispatcher)
    {
        _service = service;
        _exitForUpdate = exitForUpdate;
        CurrentVersion = AppUpdater.CurrentVersion();
        // After an update, the installer and the old exe are the way back until the installer has exited. Opened
        // now, before this version confirms it started (on first render), so the installer's id can't be reused yet.
        var installer = UpdateHandoff.OpenPrevious(Environment.GetCommandLineArgs());
        _cleanup = _exe is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using (installer) installer?.WaitForExit(UpdateHandoff.StartTimeout);
            await service.Updater.CleanUpAsync(_exe);
            ExtractionCleanup.Run();
        });
        if (_exe is not null && CurrentVersion is not null && AppUpdater.ReadFailedUpdate(_exe) is { } failed)
        {
            (_failed, var announce) = AppUpdater.ResolveFailedUpdate(CurrentVersion, failed);
            if (_failed is null) AppUpdater.SaveFailedUpdate(_exe, null); // that version (or a newer one) runs now
            else if (announce)
            {
                Stage = UpdateStage.RolledBack;
                AppUpdater.SaveFailedUpdate(_exe, _failed);
            }
        }
        _timer = new DispatcherTimer(FirstCheckDelay, DispatcherPriority.Background, async (_, _) =>
        {
            _timer!.Interval = CheckInterval;
            await CheckAsync();
        }, dispatcher);
        if (CurrentVersion is null) _timer.Stop(); // development build
    }

    /// <summary>This build's release version; null for development builds, which don't update.</summary>
    public AppVersion? CurrentVersion { get; }

    public bool IsReleaseBuild => CurrentVersion is not null;

    public string VersionText => CurrentVersion is null
        ? "Development build: updates are off."
        : $"You have Songify {CurrentVersion}. Checks GitHub twice a day; installs only when you click Update.";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title), nameof(Message), nameof(IsVisible), nameof(CanInstall), nameof(CanCancel),
         nameof(CanDismiss), nameof(DismissLabel), nameof(ReleasePageLabel), nameof(ShowProgress), nameof(IsFailed))]
    private UpdateStage _stage;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private AvailableUpdate? _update;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Message))] private double _progress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Message))] private int _busySongs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Message))] private bool _waitingForEditor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Message))] private string? _error;

    public bool IsVisible => Stage != UpdateStage.None;
    public bool CanInstall => Stage is UpdateStage.Available or UpdateStage.Failed;
    public bool CanCancel => Stage is UpdateStage.Downloading or UpdateStage.Waiting;
    public bool CanDismiss => CanInstall || Stage == UpdateStage.RolledBack;
    public string DismissLabel => Stage == UpdateStage.RolledBack ? "OK" : "Later";
    public string ReleasePageLabel => Stage == UpdateStage.RolledBack ? "Release page" : "What's new";
    public bool ShowProgress => Stage == UpdateStage.Downloading;
    public bool IsFailed => Stage is UpdateStage.Failed or UpdateStage.RolledBack;

    public string Title => Stage == UpdateStage.RolledBack
        ? $"Songify {_failed?.Version} didn't start"
        : $"Songify {Update?.Version} is available";

    public string Message => Stage switch
    {
        UpdateStage.Available => "Updating restarts Songify. Your settings, songs and queue are kept.",
        UpdateStage.Downloading => Progress < 1 ? $"Downloading… {Progress:P0}" : "Checking the download…",
        UpdateStage.Waiting when WaitingForEditor => "Waiting for you to close the song editor.",
        UpdateStage.Waiting => BusySongs switch
        {
            0 => "Getting ready to restart…",
            1 => "Waiting for 1 song to finish. New songs wait until after the update.",
            _ => $"Waiting for {BusySongs} songs to finish. New songs wait until after the update.",
        },
        UpdateStage.Restarting => "Restarting…",
        UpdateStage.Failed => $"Couldn't update: {Error?.TrimEnd('.')}. Try again, or download it from the release page.",
        UpdateStage.RolledBack => $"You're on Songify {CurrentVersion}. You'll be offered the next version when it's out.",
        _ => "",
    };

    /// <summary>Asks GitHub once, if updates are on and nothing is already shown.</summary>
    public async Task CheckAsync()
    {
        if (CurrentVersion is null || !_service.Settings.CheckForUpdates || Stage != UpdateStage.None) return;
        await _cleanup;
        var update = await _service.Updater.CheckAsync(CurrentVersion, CancellationToken.None);
        if (update is null || update.Version == _failed?.Version || Stage != UpdateStage.None || !_service.Settings.CheckForUpdates)
            return;
        Update = update;
        Stage = UpdateStage.Available;
    }

    /// <summary>Hides an offered update (update checks were turned off). An update in progress keeps going.</summary>
    public void Hide()
    {
        if (CanDismiss) Stage = UpdateStage.None;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Update is not { } update || !CanInstall) return;
        var exe = _exe;
        var problem = exe is null ? "Songify can't tell where it's running from" : AppUpdater.WhyCantReplace(exe);
        if (exe is null || problem is not null)
        {
            Error = problem;
            Stage = UpdateStage.Failed;
            return;
        }

        using var cts = _installing = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            // Download while songs keep saving; then stop new ones, wait for the rest, start the new version and
            // exit. It installs itself once this process has exited.
            await _cleanup;
            Progress = 0;
            Stage = UpdateStage.Downloading;
            var progress = new Progress<double>(p => Progress = p);
            var newExe = await _service.Updater.DownloadAsync(update, ((IProgress<double>)progress).Report, ct);

            BusySongs = 0;
            Stage = UpdateStage.Waiting;
            await WaitUntilIdleAsync(ct);
            Stage = UpdateStage.Restarting;
            if (!await Task.Run(() => UpdateHandoff.StartInstaller(newExe, exe), CancellationToken.None))
                throw new InvalidOperationException($"Songify {update.Version} didn't start");
            AppLog.Info($"Closing so Songify {update.Version} can install itself");
            _exitForUpdate();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _service.ResumeWork();
            Stage = UpdateStage.Available;
        }
        catch (Exception ex)
        {
            _service.ResumeWork();
            AppLog.Error("Update failed", ex);
            Error = ex is TaskCanceledException ? "the download timed out" : ex.Message;
            Stage = UpdateStage.Failed;
        }
        finally
        {
            _installing = null;
        }
    }

    /// <summary>No song being saved, and no song editor open (closing Songify would lose its changes).</summary>
    private async Task WaitUntilIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            await _service.HoldAndWaitUntilIdleAsync(new Progress<int>(n => BusySongs = n), ct);
            WaitingForEditor = Application.Current?.Windows.OfType<EditSongWindow>().Any() == true;
            if (!WaitingForEditor) return;
            await Task.Delay(EditorPoll, ct);
        }
    }

    [RelayCommand]
    private void Cancel() => _installing?.Cancel();

    [RelayCommand]
    private void OpenReleasePage()
    {
        // Checked to be this repository's release page when it was offered.
        var page = Stage == UpdateStage.RolledBack && _failed is not null ? AppUpdater.ReleasePage(_failed.Version) : Update?.PageUrl;
        if (page is not null) Shell.Open(page);
    }

    /// <summary>Hides the bar until the next check finds it again (twice a day, or the next start).</summary>
    [RelayCommand]
    private async Task DismissAsync()
    {
        var wasRolledBack = Stage == UpdateStage.RolledBack;
        Hide();
        if (wasRolledBack) await CheckAsync(); // the start-up check was skipped while this showed
    }
}
