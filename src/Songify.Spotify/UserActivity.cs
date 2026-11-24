using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Songify.Spotify;

/// <summary>Whether someone is using the PC: recent keyboard/mouse input, or another app playing sound.</summary>
internal static partial class UserActivity
{
    /// <summary>Quieter than this (0..1 peak) counts as not playing: open but idle streams report ~0.</summary>
    private const float AudiblePeak = 0.001f;

    /// <summary>Tick count (ms since boot, wraps) of the last keyboard or mouse input.</summary>
    public static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : unchecked((uint)Environment.TickCount);
    }

    public static TimeSpan InputIdle => TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - LastInputTick()));

    /// <summary>
    /// Away = no input for <paramref name="idle"/> and nothing audible playing (a film or call with no
    /// input isn't "away"). Includes Spotify itself: the user may be listening from across the room.
    /// </summary>
    public static bool IsAway(TimeSpan idle) => InputIdle >= idle && !AnyAppPlaying();

    private static bool AnyAppPlaying()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        var self = (uint)Environment.ProcessId;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.IsSystemSoundsSession || session.GetProcessID == self) continue;
            if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive
                && session.AudioMeterInformation.MasterPeakValue > AudiblePeak)
                return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
