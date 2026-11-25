using System.Security.Cryptography;
using System.Text;

namespace Songify.Core.Infrastructure;

public sealed class AppPaths
{
    public string DataDir { get; }
    /// <summary>Machine-specific state: tools, temp files, the recorder's mute marker.</summary>
    public string LocalDir { get; }
    public string ToolsDir { get; }
    public string WorkDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string LibraryFile => Path.Combine(DataDir, "library.json");
    public string LogFile => Path.Combine(DataDir, "songify.log");

    /// <summary>
    /// Held by the running Songify for this data folder: two instances would share the library and download every
    /// song twice. An update's installer takes it too, so it never replaces an exe that is running.
    /// </summary>
    public string SingleInstanceMutexName =>
        $@"Local\Songify-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDir.ToUpperInvariant())))[..16]}";

    public AppPaths(string dataDir, string localDir)
    {
        DataDir = dataDir;
        LocalDir = localDir;
        ToolsDir = Path.Combine(localDir, "tools");
        WorkDir = Path.Combine(localDir, "work");
    }

    /// <summary>
    /// %APPDATA%\Songify for settings/library, %LOCALAPPDATA%\Songify for tools and temp files.
    /// SONGIFY_DATA_DIR overrides the settings/library folder (for testing with a separate profile).
    /// </summary>
    public static AppPaths Default() => new(
        Environment.GetEnvironmentVariable("SONGIFY_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Songify"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songify"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ToolsDir);
        Directory.CreateDirectory(WorkDir);
    }
}
