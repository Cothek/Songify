using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Songify.Core.Spotify;

namespace Songify.Spotify;

/// <summary>
/// Records one process tree's audio (not the whole PC's) to a WAV file and keeps a loudness
/// envelope, one value per <see cref="RecordingCutter.BlockMs"/> block, for finding the song.
/// </summary>
internal sealed class ProcessCapture : IDisposable
{
    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiRecorder _recorder;
    private readonly WaveFileWriter _writer;
    private readonly List<float> _envelope = [];
    private readonly object _lock = new();
    private readonly int _samplesPerBlock;
    private double _sumSq;
    private int _samples;
    private bool _stopped;
    private bool _started;

    private ProcessCapture(WasapiRecorder recorder, string path)
    {
        _recorder = recorder;
        Format = recorder.WaveFormat;
        var isFloat = Format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (Format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
        if (!isFloat || Format.BitsPerSample != 32)
            throw new NotSupportedException($"Unexpected capture format {Format}");
        _samplesPerBlock = FramesPerBlock(Format) * Format.Channels;
        _writer = new WaveFileWriter(path, Format);
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Fail($"the capture stopped ({e.Exception.Message})");
        };
    }

    public WaveFormat Format { get; }

    /// <summary>Set when audio was lost (a glitch, or the capture died): the recording can't be trusted.</summary>
    public string? Failure { get; private set; }

    /// <summary>Blocks captured so far: the capture's clock for stamping events.</summary>
    public int Blocks
    {
        get { lock (_lock) return _envelope.Count; }
    }

    public static async Task<ProcessCapture> StartAsync(int processId, string path)
    {
        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .BuildAsync();
        try
        {
            var capture = new ProcessCapture(recorder, path);
            recorder.StartRecording();
            return capture;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    private static int FramesPerBlock(WaveFormat format) => format.SampleRate * RecordingCutter.BlockMs / 1000;

    private void Fail(string reason)
    {
        lock (_lock) Failure ??= reason;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_lock)
        {
            if (_stopped) return;
            // The first packet after starting is normally flagged as a discontinuity.
            if (_started && flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) Failure ??= "the audio glitched";
            _started = true;

            var silent = flags.HasFlag(AudioClientBufferFlags.Silent);
            try
            {
                if (silent) _writer.Write(new byte[buffer.Length]); // the buffer's contents are undefined
                else _writer.Write(buffer);
            }
            catch (IOException ex)
            {
                Failure ??= $"couldn't save the recording ({ex.Message})";
                _stopped = true;
                return;
            }

            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var i = 0; i < samples.Length; i++)
            {
                if (!silent) _sumSq += samples[i] * samples[i];
                if (++_samples < _samplesPerBlock) continue;
                _envelope.Add(RecordingCutter.Db(_sumSq, _samples));
                _sumSq = 0;
                _samples = 0;
            }
        }
    }

    /// <summary>Stops recording, closes the file and returns the envelope.</summary>
    public float[] Stop()
    {
        _recorder.StopRecording();
        lock (_lock)
        {
            _stopped = true;
            _writer.Dispose();
            return [.. _envelope];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
            _writer.Dispose();
        }
        _recorder.Dispose();
    }

    /// <summary>Copies blocks [start, end) of a capture file into a new WAV file.</summary>
    public static void Extract(string capturePath, int startBlock, int endBlock, string outputPath)
    {
        using var reader = new WaveFileReader(capturePath);
        var blockBytes = FramesPerBlock(reader.WaveFormat) * reader.WaveFormat.BlockAlign;
        reader.Position = (long)startBlock * blockBytes;
        var remaining = (long)(endBlock - startBlock) * blockBytes;
        using var writer = new WaveFileWriter(outputPath, reader.WaveFormat);
        var buffer = new byte[blockBytes * 100];
        while (remaining > 0)
        {
            var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            writer.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
