namespace Songify.Core.Infrastructure;

/// <summary>Temp folders for one job each (under the app's work folder, which is cleared at start).</summary>
public static class WorkDir
{
    /// <summary>Runs <paramref name="action"/> in a fresh folder <paramref name="root"/>\<paramref name="name"/> that is deleted afterwards.</summary>
    public static async Task<T> RunAsync<T>(string root, string name, Func<string, Task<T>> action)
    {
        var work = Path.Combine(root, name);
        Directory.CreateDirectory(work);
        try
        {
            return await action(work);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Task RunAsync(string root, string name, Func<string, Task> action) =>
        RunAsync(root, name, async work =>
        {
            await action(work);
            return true;
        });
}
