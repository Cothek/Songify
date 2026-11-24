using NAudio.CoreAudioApi;
using Songify.Core.Infrastructure;

namespace Songify.Spotify;

/// <summary>
/// Mutes the default output device while recording. Spotify's own volume can't be touched (the
/// capture follows it), but the device mute doesn't affect the capture. A marker file records which
/// device Songify muted, so a crash mid-recording is undone on the next start.
/// </summary>
internal sealed class MasterMute(string markerPath)
{
    /// <summary>Mutes the default output unless it's already muted; dispose to unmute.</summary>
    public IDisposable Mute()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (device.AudioEndpointVolume.Mute)
        {
            device.Dispose();
            return new Restore(null); // the user muted it; leave it as they had it
        }
        File.WriteAllText(markerPath, device.ID);
        device.AudioEndpointVolume.Mute = true;
        return new Restore(() =>
        {
            using (device) device.AudioEndpointVolume.Mute = false;
            File.Delete(markerPath);
        });
    }

    /// <summary>Unmutes a device left muted by a recording that never finished.</summary>
    public void RestoreAfterCrash()
    {
        if (!File.Exists(markerPath)) return;
        try
        {
            var id = File.ReadAllText(markerPath).Trim();
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            device.AudioEndpointVolume.Mute = false;
            AppLog.Info("Unmuted the sound output left muted by an interrupted Spotify recording");
        }
        catch (Exception ex) // device gone or COM failure: nothing left to restore
        {
            AppLog.Warn($"Could not restore the sound output after an interrupted recording: {ex.Message}");
        }
        try { File.Delete(markerPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // retried next start
    }

    private sealed class Restore(Action? undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            var undo = Interlocked.Exchange(ref _undo, null);
            if (undo is null) return;
            try { undo(); }
            catch (Exception ex) { AppLog.Error("Could not unmute the sound output after recording; unmute it in Windows", ex); }
        }
    }
}
