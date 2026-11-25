using System.ComponentModel;
using System.Diagnostics;
using Songify.Core.Infrastructure;

namespace Songify.Core.Updates;

/// <summary>How a started process answered <see cref="UpdateHandoff.StartAndConfirm"/>.</summary>
public enum StartResult
{
    Confirmed,
    /// <summary>It didn't start, exited, or hung and was stopped.</summary>
    Failed,
    /// <summary>It hung and couldn't be stopped: its exe is still running.</summary>
    StillRunning,
}

/// <summary>
/// The restart after an update, in three processes, so that no running Songify.exe is ever renamed or replaced (a
/// single-file exe reads parts of itself from its path while it runs):
/// <list type="number">
/// <item>The old version starts the downloaded exe from the update folder as an installer and, once it confirms, exits.</item>
/// <item>The installer waits for the old version to exit, takes the single-instance lock (so no Songify is running),
/// puts itself in place of Songify.exe (keeping .old), and starts it.</item>
/// <item>The installed version confirms once its window shows. If it exits or hangs first, the installer puts the old
/// version back (<see cref="AppUpdater.RollBack"/>) and starts it.</item>
/// </list>
/// </summary>
public static class UpdateHandoff
{
    private const string PreviousArgument = "--updated-from";
    private const string InstallArgument = "--install-into";
    /// <summary>Generous: the first start of a new exe unpacks it, and antivirus may scan it first.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(30);

    private static string EventName(int processId) => $@"Local\Songify-update-started-{processId}";

    /// <summary>
    /// Old version, before it exits: starts <paramref name="newExe"/> to install itself over <paramref name="currentExe"/>.
    /// True once it runs; it then waits for this process to exit.
    /// </summary>
    public static bool StartInstaller(string newExe, string currentExe) =>
        StartAndConfirm(new ProcessStartInfo(newExe) { ArgumentList = { InstallArgument, currentExe } }, StartTimeout) == StartResult.Confirmed;

    /// <summary>The exe an installer run replaces (the new exe's command line), or null for a normal start.</summary>
    public static string? InstallTarget(IReadOnlyList<string> args) => ValueAfter(args, InstallArgument);

    /// <summary>
    /// Installer (this process is the new version, in the update folder): installs itself over the target, starts it,
    /// and rolls back if it doesn't start. Returns a message for the user when something needs their attention.
    /// </summary>
    /// <param name="version">This version; null for a development build (no failure is noted then).</param>
    /// <param name="instanceMutexName">The running Songify's single-instance lock (<see cref="AppPaths.SingleInstanceMutexName"/>).</param>
    public static string? Install(IReadOnlyList<string> args, AppVersion? version, string instanceMutexName) =>
        Install(args, version, instanceMutexName, Environment.ProcessPath,
            exe => StartAndConfirm(new ProcessStartInfo(exe), StartTimeout));

    /// <summary><see cref="Install(IReadOnlyList{string}, AppVersion?, string)"/> from <paramref name="self"/>, starting the installed exe with <paramref name="startInstalled"/>.</summary>
    public static string? Install(IReadOnlyList<string> args, AppVersion? version, string instanceMutexName, string? self,
        Func<string, StartResult> startInstalled)
    {
        if (InstallTarget(args) is not { } target || self is null) return null;
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return null; // never replace the exe that is running
        var name = version is null ? "The new version" : $"Songify {version}";

        // Open the old version before confirming: it exits right after, and its process id could then be reused.
        using (var previous = OpenPrevious(args))
        {
            ConfirmStarted(args);
            if (previous is not null && !previous.WaitForExit(StartTimeout))
                return "Songify couldn't update because the previous version didn't finish closing. Once it has closed, start Songify again.";
        }

        var startOld = false;
        var installProblem = WithInstanceLock(instanceMutexName,
            "Songify was started again before the update finished. Close it, and it will offer the update again.", () =>
            {
                try
                {
                    AppUpdater.Install(self, target);
                    return null;
                }
                catch (InvalidOperationException ex)
                {
                    AppLog.Error("Couldn't install the update", ex);
                    return ex.Message; // the swap couldn't be undone; says how to fix it by hand
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Couldn't install the update", ex);
                    startOld = true; // the version you had, untouched
                    return $"Couldn't update to {name}: {ex.Message}";
                }
            });
        if (installProblem is not null)
        {
            if (startOld) TryStart(target);
            return installProblem;
        }
        AppLog.Info($"Installed {name}");

        switch (startInstalled(target))
        {
            case StartResult.Confirmed:
                return null;
            case StartResult.StillRunning:
                return $"{name} didn't start and couldn't be stopped. Restart Windows, then: {AppUpdater.ManualRestoreHint(target)}.";
        }

        AppLog.Info($"{name} didn't start; going back to the previous version");
        var rollbackProblem = WithInstanceLock(instanceMutexName,
            $"{name} didn't start, and Songify was started again meanwhile. If it doesn't work: {AppUpdater.ManualRestoreHint(target)}.", () =>
            {
                try
                {
                    AppUpdater.RollBack(target, version?.Core);
                    return null;
                }
                catch (FileNotFoundException ex)
                {
                    AppLog.Error("Couldn't go back to the previous version", ex);
                    return $"{name} didn't start, and the previous version is gone. Download it from {AppUpdater.ReleasesPage}.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Couldn't go back to the previous version", ex);
                    return $"{name} didn't start, and Songify couldn't go back to the previous version.\n\n{AppUpdater.ManualRestoreHint(target)}.";
                }
            });
        return rollbackProblem ?? (TryStart(target) ? null : $"{name} didn't start, so Songify went back to the previous version. Start Songify again.");
    }

    /// <summary>
    /// Runs <paramref name="swap"/> holding the single-instance lock, so no Songify is running from the exe it renames
    /// (only Songify for the same data folder takes this lock). Released and closed before Songify is started again.
    /// Returns <paramref name="whenHeld"/> without running it if a Songify holds the lock, else what it returns.
    /// </summary>
    private static string? WithInstanceLock(string name, string whenHeld, Func<string?> swap)
    {
        if (InstanceLock.TryAcquire(name) is not { } instance) return whenHeld;
        try
        {
            return swap();
        }
        finally
        {
            InstanceLock.Release(instance);
        }
    }

    /// <summary>Starts <paramref name="exe"/>, telling it this process started it so it leaves the update files alone until this one exits.</summary>
    private static bool TryStart(string exe)
    {
        using var process = TryStart(WithPrevious(new ProcessStartInfo(exe)));
        return process is not null;
    }

    private static Process? TryStart(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Couldn't start {start.FileName}", ex);
            return null;
        }
    }

    private static ProcessStartInfo WithPrevious(ProcessStartInfo start)
    {
        start.ArgumentList.Add(PreviousArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        return start;
    }

    /// <summary>
    /// Starts <paramref name="start"/> and waits until it confirms (<see cref="ConfirmStarted"/>), exits, or hangs for
    /// <paramref name="timeout"/> (it's then stopped).
    /// </summary>
    public static StartResult StartAndConfirm(ProcessStartInfo start, TimeSpan timeout)
    {
        EventWaitHandle started;
        try
        {
            started = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(Environment.ProcessId));
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            AppLog.Error("Couldn't prepare the restart", ex);
            return StartResult.Failed;
        }

        using (started)
        using (var process = TryStart(WithPrevious(start)))
        {
            if (process is null) return StartResult.Failed;
            var waited = Stopwatch.StartNew();
            while (!started.WaitOne(Poll))
            {
                if (process.HasExited)
                {
                    if (started.WaitOne(0)) return StartResult.Confirmed; // confirmed, then exited (another Songify was running)
                    AppLog.Error($"{Path.GetFileName(start.FileName)} exited with code {process.ExitCode} before it was running");
                    return StartResult.Failed;
                }
                if (waited.Elapsed > timeout)
                {
                    AppLog.Error($"{Path.GetFileName(start.FileName)} wasn't running within {timeout.TotalSeconds:0} seconds; stopping it");
                    return Stop(process) ? StartResult.Failed : StartResult.StillRunning;
                }
            }
            return StartResult.Confirmed;
        }
    }

    /// <summary>Ends a hung process (only it: anything it started, e.g. Spotify, keeps running). True once it's gone.</summary>
    private static bool Stop(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            AppLog.Warn($"Couldn't stop it: {ex.Message}");
        }
        if (process.WaitForExit(KillWait)) return true;
        AppLog.Error("It is still running after being stopped");
        return false;
    }

    /// <summary>The process that started this one in an update, from the command line.</summary>
    public static int? PreviousProcessId(IReadOnlyList<string> args) =>
        int.TryParse(ValueAfter(args, PreviousArgument), out var processId) && processId > 0 ? processId : null;

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    /// <summary>
    /// The process that started this one in an update, if it's still running. Open it before confirming: once confirmed
    /// it exits, and its process id could be reused.
    /// </summary>
    public static Process? OpenPrevious(IReadOnlyList<string> args)
    {
        if (PreviousProcessId(args) is not { } processId) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            // GetProcessById only checks the id; this opens a handle (kept by the Process), which pins this process.
            _ = process.SafeHandle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return null; // already gone
        }
    }

    /// <summary>Tells the process that started this one in an update that this one is running.</summary>
    public static void ConfirmStarted(IReadOnlyList<string> args)
    {
        if (PreviousProcessId(args) is not { } processId || !EventWaitHandle.TryOpenExisting(EventName(processId), out var started)) return;
        using (started) started.Set();
    }
}
