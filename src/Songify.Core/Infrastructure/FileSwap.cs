namespace Songify.Core.Infrastructure;

/// <summary>
/// Replaces a file with a new version so that it is never left half-written: the new version is first copied next to
/// the target (so the final step is a rename on one drive, even when the new version is on another), then renamed
/// over it. If that fails (the file is open in a player, or read-only), the target is left untouched.
/// </summary>
public static class FileSwap
{
    private const string StagedPattern = "~songify-*.tmp";
    private static readonly TimeSpan StaleStagedAge = TimeSpan.FromHours(1);

    public const string InUseMessage =
        "Couldn't change the file. Close it in any app that's playing it, check that it isn't read-only, and try again.";

    /// <summary>Puts a copy of <paramref name="source"/> at <paramref name="target"/>, replacing any file there.</summary>
    /// <exception cref="InvalidOperationException">The target couldn't be replaced (<see cref="InUseMessage"/>).</exception>
    public static void Replace(string source, string target)
    {
        string? staged = null;
        try
        {
            staged = Stage(source, target);
            File.Move(staged, target, overwrite: true);
        }
        catch (Exception ex) when (staged is not null && ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(InUseMessage, ex);
        }
        finally
        {
            if (staged is not null) TryDelete(staged); // already gone after a successful swap
        }
    }

    /// <summary>
    /// Puts a copy of <paramref name="source"/> at <paramref name="target"/>, after moving the current target out of
    /// the way with <paramref name="recycle"/> (to the Recycle Bin). <paramref name="source"/> is deleted once it's in
    /// place. If <paramref name="recycle"/> throws, nothing has changed. If the copy can't be put in place after the
    /// target was recycled, it's kept next to the target as "&lt;name&gt; (levelled)" and the error says so.
    /// </summary>
    public static void ReplaceRecycling(string source, string target, Action<string> recycle)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A file can't replace itself.");
        string? staged = null;
        try
        {
            staged = Stage(source, target);
            recycle(target);
        }
        catch
        {
            if (staged is not null) TryDelete(staged); // nothing has changed
            throw;
        }

        // The original is gone from its folder now: from here on the staged copy must never be deleted.
        try
        {
            File.Move(staged, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var kept = KeepAside(staged, target);
            throw new InvalidOperationException(
                $"The original is in the Recycle Bin, but the new version couldn't take its place. It's saved as {Path.GetFileName(kept)}.", ex);
        }
        TryDelete(source);
    }

    /// <summary>Renames a staged copy to "&lt;name&gt; (levelled)&lt;ext&gt;" next to the target; returns where it is.</summary>
    private static string KeepAside(string staged, string target)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var name = Path.GetFileNameWithoutExtension(target) + " (levelled)";
        var extension = Path.GetExtension(target);
        for (var n = 1; n < 100; n++)
        {
            var path = Path.Combine(folder, (n == 1 ? name : $"{name} {n}") + extension);
            try
            {
                File.Move(staged, path);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
        }
        return staged; // stays under its staged name (old ones get cleaned up, so say where it is)
    }

    private static string Stage(string source, string target)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(folder);
        DeleteStaleStagedFiles(folder);
        var staged = Path.Combine(folder, StagedPattern.Replace("*", Guid.NewGuid().ToString("N")));
        try
        {
            File.Copy(source, staged);
            File.SetLastWriteTimeUtc(staged, DateTime.UtcNow); // a copy keeps the source's time; it isn't stale
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        return staged;
    }

    /// <summary>Staged copies left by a crash mid-swap; old ones only, so another swap's copy is never touched.</summary>
    private static void DeleteStaleStagedFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, StagedPattern))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleStagedAge) TryDelete(file);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
