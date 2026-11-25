using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Tools;

namespace Songify.Core.Audio;

/// <summary>How loud a file is (EBU R128): its integrated loudness and its true peak.</summary>
public sealed record Loudness(double IntegratedLufs, double TruePeakDb);

/// <summary>How a file's volume is changed.</summary>
public enum LevelMethod
{
    /// <summary>MP3: each frame's global gain, in 1.5 dB steps. No re-encoding.</summary>
    Mp3Gain,
    /// <summary>Ogg Opus: the header's output gain. No re-encoding.</summary>
    OpusHeader,
    /// <summary>FLAC, WAV: the samples are scaled and stored again, still lossless.</summary>
    Rewrite,
    /// <summary>AAC, Ogg Vorbis: decoded and encoded again at the same bitrate, which loses a little quality.</summary>
    Reencode,
}

/// <param name="CodecArgs">ffmpeg's codec arguments for <see cref="LevelMethod.Rewrite"/> and <see cref="LevelMethod.Reencode"/>.</param>
public sealed record LevelAnalysis(Loudness Loudness, LevelMethod Method, IReadOnlyList<string> CodecArgs);

/// <summary>
/// Makes songs play equally loud in any player by changing their volume (not with ReplayGain tags, which many
/// players ignore): measures each file's loudness with ffmpeg, then turns it up or down toward a target, losslessly
/// where the format allows it.
/// </summary>
public sealed partial class VolumeLeveller(AudioEncoder encoder, ToolManager tools, string workRoot)
{
    /// <summary>Files it can level, by extension.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav"];

    /// <summary>A song is never turned up so far that its peaks go above this (dBTP), so it can't clip.</summary>
    public const double PeakCeilingDb = -1.0;

    /// <summary>Smaller changes aren't worth rewriting a file for.</summary>
    public const double MinimumChangeDb = 0.5;

    /// <summary>ebur128 reports silence as -70 LUFS.</summary>
    private const double SilenceLufs = -69.9;

    private const int FallbackKbps = 192;

    /// <summary>Reads what the file is and measures how loud it is (takes a second or two per song).</summary>
    /// <exception cref="InvalidOperationException">It can't be levelled; the message says why.</exception>
    public async Task<LevelAnalysis> AnalyzeAsync(string path, CancellationToken ct)
    {
        var probe = await ProcessRunner.RunAsync(tools.Ffprobe,
        [
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_fmt,bit_rate:format=format_name,bit_rate", "-of", "json", path,
        ], ct);
        if (probe.ExitCode != 0)
        {
            AppLog.Warn($"Couldn't read {Path.GetFileName(path)}: {probe.ErrorSummary}");
            throw new InvalidOperationException("Not a readable audio file.");
        }
        var (method, codecArgs) = MethodFor(Path.GetExtension(path), probe.StdOut);

        var measured = await ProcessRunner.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:a:0",
            "-af", "ebur128=peak=true:framelog=verbose", "-f", "null", "-",
        ], ct);
        if (measured.ExitCode != 0)
        {
            AppLog.Warn($"Couldn't measure {Path.GetFileName(path)}: {measured.ErrorSummary}");
            throw new InvalidOperationException("Couldn't measure it: the audio may be damaged.");
        }
        var loudness = ParseLoudness(measured.StdErr)
                       ?? throw new InvalidOperationException("Couldn't measure it: the audio may be damaged.");
        if (loudness.IntegratedLufs <= SilenceLufs) throw new InvalidOperationException("Silent, or too quiet to measure.");
        return new LevelAnalysis(loudness, method, codecArgs);
    }

    /// <summary>How a file is levelled, from its extension and ffprobe's JSON for its first audio stream.</summary>
    public static (LevelMethod Method, IReadOnlyList<string> CodecArgs) MethodFor(string extension, string probeJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(probeJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Not a readable audio file.");
        }
        var stream = (root?["streams"] as JsonArray)?.FirstOrDefault();
        var codec = JsonValues.Text(stream, "codec_name") ?? "";
        var container = JsonValues.Text(root?["format"], "format_name") ?? "";
        var kbps = Kbps(JsonValues.Text(stream, "bit_rate")) ?? Kbps(JsonValues.Text(root?["format"], "bit_rate")) ?? FallbackKbps;
        IReadOnlyList<string> Lossy(AudioFormat format) => AudioEncoder.PlanOutput(format, kbps, null, null).AudioArgs;

        return (codec, extension.ToLowerInvariant()) switch
        {
            ("mp3", ".mp3") => (LevelMethod.Mp3Gain, []),
            ("opus", ".opus" or ".ogg") when container.Contains("ogg") => (LevelMethod.OpusHeader, []),
            ("flac", ".flac") => (LevelMethod.Rewrite,
                ["-c:a", "flac", "-sample_fmt", JsonValues.Text(stream, "sample_fmt") is "s16" or "s16p" ? "s16" : "s32"]),
            (_, ".wav") when codec.StartsWith("pcm_", StringComparison.Ordinal) => (LevelMethod.Rewrite, ["-c:a", codec]),
            ("aac", ".m4a") => (LevelMethod.Reencode, Lossy(AudioFormat.Aac)),
            ("vorbis", ".ogg") => (LevelMethod.Reencode, Lossy(AudioFormat.Vorbis)),
            _ => throw new InvalidOperationException(
                $"Can't level {(codec.Length > 0 ? codec : "this")} audio in a {extension.TrimStart('.').ToUpperInvariant()} file."),
        };
    }

    /// <summary>Bitrate in kbps, rounded to a multiple of 8 and kept within what the encoders take.</summary>
    private static int? Kbps(string? bitsPerSecond) =>
        double.TryParse(bitsPerSecond, NumberStyles.Float, CultureInfo.InvariantCulture, out var bps) && bps > 0
            ? Math.Clamp((int)Math.Round(bps / 8000) * 8, 32, 320)
            : null;

    /// <summary>The integrated loudness and true peak from the summary ffmpeg's ebur128 filter prints last.</summary>
    public static Loudness? ParseLoudness(string stderr)
    {
        var summary = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (summary < 0) return null;
        var text = stderr[summary..];
        var integrated = IntegratedPattern().Match(text);
        if (!integrated.Success) return null;
        var peak = PeakPattern().Match(text);
        double Number(Match m) => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return new Loudness(Number(integrated),
            peak.Success && peak.Groups[1].Value != "-inf" ? Number(peak) : double.NegativeInfinity);
    }

    [GeneratedRegex(@"I:\s+(-?\d+(?:\.\d+)?) LUFS")]
    private static partial Regex IntegratedPattern();

    [GeneratedRegex(@"Peak:\s+(-inf|-?\d+(?:\.\d+)?) dBFS")]
    private static partial Regex PeakPattern();

    /// <summary>
    /// The change in dB that brings a song to <paramref name="targetLufs"/>: never so far up that its peaks pass
    /// <see cref="PeakCeilingDb"/>, and in whole steps for MP3. 0 when it's already within
    /// <see cref="MinimumChangeDb"/> (or an MP3 step) of the target.
    /// </summary>
    public static double PlanGain(LevelAnalysis analysis, double targetLufs)
    {
        var gain = targetLufs - analysis.Loudness.IntegratedLufs;
        if (gain > 0) gain = Math.Min(gain, Math.Max(0, PeakCeilingDb - analysis.Loudness.TruePeakDb));
        if (analysis.Method == LevelMethod.Mp3Gain)
            // Up: round down, so the peak limit holds. Down: to the nearest step.
            return (gain > 0 ? Math.Floor(gain / Mp3Gain.StepDb) : Math.Round(gain / Mp3Gain.StepDb)) * Mp3Gain.StepDb;
        return Math.Abs(gain) < MinimumChangeDb ? 0 : Math.Round(gain, 1);
    }

    /// <summary>
    /// Writes <paramref name="source"/>, <paramref name="gainDb"/> louder (or quieter), to <paramref name="copy"/>
    /// (replacing any file there), or when that's null in place of <paramref name="source"/>, whose original then goes
    /// to the Recycle Bin through <paramref name="recycle"/>. Tags and cover are kept, except gain tags (ReplayGain and
    /// the like), which would now be wrong. Returns the change applied in dB.
    /// </summary>
    public Task<double> LevelAsync(
        string source, string? copy, LevelAnalysis analysis, double gainDb, Action<string> recycle, CancellationToken ct) =>
        WorkDir.RunAsync(workRoot, "level-" + Guid.NewGuid().ToString("N"), async work =>
        {
            var extension = Path.GetExtension(source);
            var current = await encoder.ReadTagsAsync(source, ct, fileNameAsTitle: false);
            var kept = current with
            {
                Other = current.Other.Where(t => !IsLevelTag(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.OrdinalIgnoreCase),
            };
            var output = Path.Combine(work, "levelled" + extension);
            double applied;
            if (analysis.Method is LevelMethod.Mp3Gain or LevelMethod.OpusHeader)
            {
                var bytes = await File.ReadAllBytesAsync(source, ct);
                applied = analysis.Method == LevelMethod.Mp3Gain
                    ? Mp3Gain.Adjust(bytes, (int)Math.Round(gainDb / Mp3Gain.StepDb)) * Mp3Gain.StepDb
                    : OpusGain.Adjust(bytes, gainDb);
                if (kept.Other.Count == current.Other.Count)
                    await File.WriteAllBytesAsync(output, bytes, ct);
                else
                {
                    // Drop the stale gain tags: copy the audio as it is into a freshly tagged file.
                    var adjusted = Path.Combine(work, "adjusted" + extension);
                    await File.WriteAllBytesAsync(adjusted, bytes, ct);
                    await encoder.RewriteAsync(adjusted, output, AudioEncoder.PlanRetag(extension), kept, current.Tags, work, ct);
                }
            }
            else
            {
                var volume = string.Create(CultureInfo.InvariantCulture, $"volume={gainDb:0.##}dB");
                var plan = AudioEncoder.PlanRetag(extension) with { AudioArgs = ["-af", volume, .. analysis.CodecArgs] };
                await encoder.RewriteAsync(source, output, plan, kept, current.Tags, work, ct);
                applied = gainDb;
            }

            await Task.Run(() =>
            {
                if (copy is null) FileSwap.ReplaceRecycling(output, source, recycle);
                else FileSwap.Replace(output, copy);
            }, ct);
            AppLog.Info($"Levelled {Path.GetFileName(source)} by {applied:+0.0;-0.0} dB");
            return applied;
        });
    /// <summary>Puts a levelled copy in place of its original, which goes to the Recycle Bin. The copy is then gone.</summary>
    public static Task ReplaceWithCopyAsync(string original, string copy, Action<string> recycle) =>
        Task.Run(() => FileSwap.ReplaceRecycling(copy, original, recycle));

    /// <summary>Tags that tell players how much to turn a song up or down: wrong once its volume has changed.</summary>
    public static bool IsLevelTag(string key) =>
        key.StartsWith("replaygain_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("r128_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("mp3gain_", StringComparison.OrdinalIgnoreCase)
        || key.Equals("iTunNORM", StringComparison.OrdinalIgnoreCase);
}
