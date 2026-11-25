namespace Songify.Core.Infrastructure;

/// <summary>Minimal app-wide log: appends to a file and raises <see cref="Line"/> for the UI.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string? FilePath { get; set; }
    public static event Action<string>? Line;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);

    /// <summary>The UI line gets the message; the log file also gets the stack trace.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.Message}", ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level,-5} {message}";
        lock (Gate)
        {
            if (FilePath is not null)
            {
                try
                {
                    var text = $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}";
                    if (ex is not null) text += ex + Environment.NewLine;
                    // Shared: during an update two Songify processes write here at once.
                    using var file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    file.Write(System.Text.Encoding.UTF8.GetBytes(text));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Logging must never crash the app.
                }
            }
        }
        Line?.Invoke(line);
    }
}
