namespace Songify.Core.Spotify;

/// <summary>
/// Finds the song inside a Spotify capture from a loudness envelope (one dBFS value per
/// <see cref="BlockMs"/> block). Spotify's "now playing" change arrives ~0.2 s before the song's
/// first sound, and silence between tracks is digital silence, so the song is the first sound after
/// a gap near that change, up to the last sound before the next change.
/// </summary>
public static class RecordingCutter
{
    public const int BlockMs = 10;

    /// <summary>Quieter than this is silence. Spotify's gaps are digital zero; fades stay well above this.</summary>
    public const double SilenceDb = -65;

    /// <summary>How far before the change the song may start: covers the change arriving a little late.</summary>
    private const int LookBackBlocks = 500 / BlockMs;

    /// <summary>How far after the change the song's first sound may be.</summary>
    private const int LookAheadBlocks = 1000 / BlockMs;

    /// <summary>When the change arrived very late, how far back to look for the gap before the song.</summary>
    private const int LateChangeBlocks = 3000 / BlockMs;

    /// <summary>A gap at least this long separates two tracks (e.g. an ad's tail and the song).</summary>
    private const int GapBlocks = 150 / BlockMs;

    /// <summary>
    /// [Start, End) in blocks, or null when there's no sound. <paramref name="songChange"/> is the block where
    /// Spotify reported the song; <paramref name="nextChange"/> where it reported anything else (or capture end).
    /// </summary>
    public static (int Start, int End)? FindSong(IReadOnlyList<float> blockDb, int songChange, int nextChange)
    {
        nextChange = Math.Min(nextChange, blockDb.Count);
        songChange = Math.Clamp(songChange, 0, nextChange);
        if (songChange >= nextChange) return null;

        var onsets = Onsets(blockDb, Math.Max(0, songChange - LateChangeBlocks), Math.Min(nextChange, songChange + LookAheadBlocks));
        // Normally: the first sound after a gap shortly before/after the change (skips an ad's tail).
        // Change reported late: the song was already playing, so take the nearest gap before it.
        // No gap at all (sound runs straight through): cut at the change.
        int? start = onsets.Where(o => o >= songChange - LookBackBlocks).Cast<int?>().FirstOrDefault()
                     ?? onsets.Cast<int?>().LastOrDefault()
                     ?? FirstSound(blockDb, songChange, nextChange);
        if (start is null) return null;

        var end = nextChange;
        while (end > start && IsSilent(blockDb[end - 1])) end--;
        return end > start ? (start.Value, end) : null;
    }

    /// <summary>
    /// Blocks in [from, to) where sound follows at least <see cref="GapBlocks"/> of silence. The capture
    /// starts before Spotify plays anything, so before block 0 counts as silence.
    /// </summary>
    private static List<int> Onsets(IReadOnlyList<float> blockDb, int from, int to)
    {
        var onsets = new List<int>();
        var scanFrom = Math.Max(0, from - GapBlocks);
        var silentRun = scanFrom == 0 ? GapBlocks : 0;
        for (var i = scanFrom; i < to; i++)
        {
            if (IsSilent(blockDb[i])) { silentRun++; continue; }
            if (i >= from && silentRun >= GapBlocks) onsets.Add(i);
            silentRun = 0;
        }
        return onsets;
    }

    private static int? FirstSound(IReadOnlyList<float> blockDb, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (!IsSilent(blockDb[i])) return i;
        return null;
    }

    public static bool IsSilent(float db) => db < SilenceDb;

    /// <summary>Loudness of interleaved float samples in dBFS (RMS); silence is <see cref="float.NegativeInfinity"/>.</summary>
    public static float Db(double sumOfSquares, long sampleCount) =>
        sampleCount == 0 || sumOfSquares <= 0 ? float.NegativeInfinity : (float)(10 * Math.Log10(sumOfSquares / sampleCount));
}
