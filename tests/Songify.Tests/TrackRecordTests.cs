using Songify.Core.Models;

namespace Songify.Tests;

public class TrackRecordTests
{
    private static TrackRecord NewTrack() => new() { VideoId = "dQw4w9WgXcQ", OriginalTitle = "Song" };

    [Fact]
    public void Only_one_concurrent_claim_wins()
    {
        for (var round = 0; round < 200; round++)
        {
            var track = NewTrack();
            var wins = 0;
            Parallel.For(0, 8, _ => { if (track.TryClaim()) Interlocked.Increment(ref wins); });

            Assert.Equal(1, wins);
            Assert.Equal(TrackStatus.Matching, track.Status);
        }
    }

    [Fact]
    public void Retry_only_resets_failed_tracks()
    {
        var done = NewTrack();
        done.Status = TrackStatus.Done;
        done.FilePath = @"C:\music\a.mp3";
        Assert.False(done.TryResetForRetry());
        Assert.Equal(@"C:\music\a.mp3", done.FilePath);

        var failed = NewTrack();
        failed.Status = TrackStatus.Failed;
        failed.Note = "boom";
        Assert.True(failed.TryResetForRetry());
        Assert.Equal(TrackStatus.Queued, failed.Status);
        Assert.Null(failed.Note);
    }
}
