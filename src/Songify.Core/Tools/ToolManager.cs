using System.IO.Compression;
using Songify.Core.Infrastructure;

namespace Songify.Core.Tools;

/// <summary>
/// Downloads and keeps the external tools Songify drives: yt-dlp (YouTube access),
/// ffmpeg/ffprobe (encoding), and Deno (JavaScript runtime yt-dlp needs for YouTube).
/// </summary>
public sealed class ToolManager(string toolsDir, HttpClient http)
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string DenoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    public string ToolsDir { get; } = toolsDir;
    public string YtDlp => Path.Combine(ToolsDir, "yt-dlp.exe");
    public string Ffmpeg => Path.Combine(ToolsDir, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(ToolsDir, "ffprobe.exe");
    public string Deno => Path.Combine(ToolsDir, "deno.exe");

    public async Task EnsureAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        if (!File.Exists(YtDlp))
            await DownloadFileAsync(YtDlpUrl, YtDlp, "yt-dlp", progress, ct);
        if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
            await DownloadFromZipAsync(FfmpegZipUrl, ["ffmpeg.exe", "ffprobe.exe"], "ffmpeg", progress, ct);
        if (!File.Exists(Deno))
            await DownloadFromZipAsync(DenoZipUrl, ["deno.exe"], "Deno", progress, ct);
    }

    /// <summary>YouTube changes often; yt-dlp must stay current or downloads start failing.</summary>
    public async Task UpdateYtDlpAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(YtDlp, ["-U"], ct);
        var summary = ProcessResult.Lines(result.StdOut).LastOrDefault() ?? "";
        if (result.ExitCode == 0) AppLog.Info($"yt-dlp update check: {summary}");
        else AppLog.Warn($"yt-dlp update failed: {result.ErrorSummary}");
    }

    private async Task DownloadFileAsync(string url, string destination, string label, IProgress<string>? progress, CancellationToken ct)
    {
        AppLog.Info($"Downloading {label} from {url}");
        await HttpDownload.ToFileAsync(http, url, destination, (done, total) => progress?.Report(total > 0
            ? $"Downloading {label}… {done * 100 / total.Value}%"
            : $"Downloading {label}… {done / 1_048_576} MB"), ct);
    }

    private async Task DownloadFromZipAsync(string url, string[] entryNames, string label, IProgress<string>? progress, CancellationToken ct)
    {
        var zipPath = Path.Combine(ToolsDir, $"{label}.zip");
        try
        {
            await DownloadFileAsync(url, zipPath, label, progress, ct);
            progress?.Report($"Unpacking {label}…");
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var name in entryNames)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"{name} not found in {url}");
                var destination = Path.Combine(ToolsDir, name);
                entry.ExtractToFile(destination + ".download", overwrite: true);
                File.Move(destination + ".download", destination, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }
}
