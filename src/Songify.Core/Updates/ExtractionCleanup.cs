using System.ComponentModel;
using System.Diagnostics;
using Songify.Core.Infrastructure;

namespace Songify.Core.Updates;

/// <summary>
/// The single-file Songify.exe unpacks WPF's native DLLs (~8 MB) to %TEMP%\.net\Songify\&lt;hash&gt;, a new folder
/// for every build, and .NET never removes them. This deletes the folders of other builds that no running Songify uses.
/// </summary>
public static class ExtractionCleanup
{
    /// <summary>A native DLL every WPF process loads from its extraction folder.</summary>
    private const string MarkerDll = "PresentationNative_cor3.dll";
    /// <summary>A folder this new may belong to a Songify that is starting right now and hasn't loaded its DLLs yet.</summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    public static void Run()
    {
        try
        {
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is not { } appName) return;
            // Only a single-file build has one: ...\.net\<app>\<hash>, among the folders .NET loads native DLLs from.
            var own = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.TrimEndingDirectorySeparator(d))
                .FirstOrDefault(d => IsExtractionFolder(d, appName));
            if (own is null) return;

            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcessesByName(appName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.HasExited) continue;
                    // A Songify whose folder we can't see might be using any of them: leave them all.
                    if (ExtractionFolder(process, appName) is not { } folder) return;
                    inUse.Add(Path.GetFileName(folder));
                }
            }

            var deleted = 0;
            foreach (var stale in StaleFolders(own, inUse, DateTime.UtcNow))
            {
                try
                {
                    Directory.Delete(stale, recursive: true);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (deleted > 0) AppLog.Info($"Deleted {deleted} folder(s) of files unpacked by earlier versions");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Couldn't clean up files unpacked by earlier versions: {ex.Message}");
        }
    }

    /// <summary>The folder <paramref name="process"/> loaded WPF's native DLL from, if it's an extraction folder for this app.</summary>
    private static string? ExtractionFolder(Process process, string appName)
    {
        try
        {
            var dll = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(Path.GetFileName(m.FileName), MarkerDll, StringComparison.OrdinalIgnoreCase));
            var folder = Path.GetDirectoryName(dll?.FileName);
            return folder is not null && IsExtractionFolder(folder, appName) ? folder : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null; // exited, or not ours to inspect
        }
    }

    /// <summary>...\.net\&lt;app&gt;\&lt;hash&gt;</summary>
    public static bool IsExtractionFolder(string folder, string appName)
    {
        var app = Path.GetDirectoryName(folder);
        return string.Equals(Path.GetFileName(app), appName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(Path.GetDirectoryName(app)), ".net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folders next to <paramref name="ownFolder"/> that no running Songify uses (<paramref name="inUse"/>, by
    /// folder name) and that are old enough not to be one being unpacked right now.
    /// </summary>
    public static IEnumerable<string> StaleFolders(string ownFolder, IReadOnlySet<string> inUse, DateTime nowUtc)
    {
        var appFolder = Path.GetDirectoryName(ownFolder)!;
        var ownName = Path.GetFileName(ownFolder);
        return Directory.EnumerateDirectories(appFolder)
            .Where(d => !string.Equals(Path.GetFileName(d), ownName, StringComparison.OrdinalIgnoreCase)
                        && !inUse.Contains(Path.GetFileName(d))
                        && nowUtc - Directory.GetCreationTimeUtc(d) > MinimumAge)
            .ToList();
    }
}
