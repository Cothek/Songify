using System.Windows;
using Songify.Core.Infrastructure;
using Songify.Core.Processing;
using Songify.Core.Updates;
using Songify.Spotify;

namespace Songify.App;

public partial class App : Application
{
    private SongifyService? _service;
    private ThemeSwitcher? _themes;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unexpected error", args.Exception);
            MessageBox.Show(args.Exception.Message, "Songify hit an unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unexpected background error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved background error", args.Exception);
            args.SetObserved();
        };

        var paths = AppPaths.Default();

        // Started by the old version to install this one (see UpdateHandoff): no window, and it always exits.
        if (UpdateHandoff.InstallTarget(e.Args) is not null)
        {
            AppLog.FilePath = paths.LogFile;
            try
            {
                if (UpdateHandoff.Install(e.Args, AppUpdater.CurrentVersion(), paths.SingleInstanceMutexName) is { } problem)
                    ShowError(problem);
            }
            catch (Exception ex)
            {
                AppLog.Error("The update failed", ex);
                ShowError($"The update failed: {ex.Message}");
            }
            finally
            {
                Shutdown();
            }
            return;
        }

        _singleInstance = InstanceLock.TryAcquire(paths.SingleInstanceMutexName);
        if (_singleInstance is null)
        {
            UpdateHandoff.ConfirmStarted(e.Args); // this version runs; another Songify was started meanwhile
            MessageBox.Show("Songify is already running. Look for it in the taskbar.", "Songify",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _service = new SongifyService(paths, new SpotifyRecorder(paths.LocalDir));
            _themes = new ThemeSwitcher();
            _themes.Apply(_service.Settings.Theme); // before the window exists, so it never flashes the wrong theme
            var window = new MainWindow(new MainViewModel(_service, _themes, Dispatcher, () => Shutdown()));
            MainWindow = window;
            // After an update: tells the installer this version works (it rolls back otherwise).
            void Confirm(object? sender, EventArgs args)
            {
                window.ContentRendered -= Confirm;
                UpdateHandoff.ConfirmStarted(e.Args);
            }
            window.ContentRendered += Confirm;
            window.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error("Songify could not start", ex);
            MessageBox.Show($"Songify could not start: {ex.Message}\n\nSettings folder: {paths.DataDir}",
                "Songify", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Off the UI thread: tool downloads and the yt-dlp update would otherwise run their continuations here.
        _ = Task.Run(_service.StartAsync);
    }

    private static void ShowError(string message) => MessageBox.Show(message, "Songify", MessageBoxButton.OK, MessageBoxImage.Error);

    protected override void OnExit(ExitEventArgs e)
    {
        _themes?.Dispose();
        // Run off the UI thread so awaits inside DisposeAsync can't deadlock on the dispatcher.
        if (_service is not null)
            Task.Run(() => _service.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        if (_singleInstance is not null) InstanceLock.Release(_singleInstance);
        base.OnExit(e);
    }
}
