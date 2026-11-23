using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Songify.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Last "ERROR:" line from stderr, or the last non-empty stderr line.</summary>
    public string ErrorSummary
    {
        get
        {
            var lines = Lines(StdErr);
            return lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                   ?? lines.LastOrDefault()
                   ?? $"exit code {ExitCode}";
        }
    }
}

public static class ProcessRunner
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                // Wait so the caller can delete the files the process had open.
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                AppLog.Warn($"Could not stop {Path.GetFileName(exe)} cleanly: {ex.Message}");
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
