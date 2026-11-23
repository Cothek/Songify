using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Songify.Core.Infrastructure;
using Songify.Core.Processing;

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

        // Two instances would share the library and download every song twice.
        _singleInstance = new Mutex(true, $@"Local\Songify-{Hash(paths.DataDir)}", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Songify is already running. Look for it in the taskbar.", "Songify",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            _service = new SongifyService(paths);
            _themes = new ThemeSwitcher();
            _themes.Apply(_service.Settings.Theme); // before the window exists, so it never flashes the wrong theme
            var window = new MainWindow(new MainViewModel(_service, _themes, Dispatcher));
            MainWindow = window;
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

    protected override void OnExit(ExitEventArgs e)
    {
        _themes?.Dispose();
        // Run off the UI thread so awaits inside DisposeAsync can't deadlock on the dispatcher.
        if (_service is not null)
            Task.Run(() => _service.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..16];
}
