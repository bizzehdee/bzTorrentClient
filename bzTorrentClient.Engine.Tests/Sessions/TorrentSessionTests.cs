using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Tests.Testing;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class TorrentSessionTests
{
    private static TorrentSession CreateSession(int pieceCount = 4) =>
        new(TorrentAddSource.Magnet.FromInfoHash("0123456789abcdef0123456789abcdef01234567"),
            MagnetLinkMetadataWithPieces(pieceCount),
            "/downloads");

    // The library's magnet-stub metadata has zero pieces, so tests that need
    // a non-zero PieceHashes.Count build a minimal fake IMetadata instead.
    private static bzTorrent.Data.IMetadata MagnetLinkMetadataWithPieces(int pieceCount) =>
        new FakeMetadata(pieceCount);

    [Fact]
    public void NewSession_StartsInPausedState()
    {
        var session = CreateSession();
        Assert.Equal(TorrentState.Paused, session.State);
    }

    [Fact]
    public void Start_FromPaused_TransitionsToActive()
    {
        var session = CreateSession();
        session.Start();
        Assert.Equal(TorrentState.Active, session.State);
    }

    [Fact]
    public void Pause_FromActive_TransitionsToPaused()
    {
        var session = CreateSession();
        session.Start();
        session.Pause();
        Assert.Equal(TorrentState.Paused, session.State);
    }

    [Fact]
    public void Stop_FromActive_TransitionsToStopped()
    {
        var session = CreateSession();
        session.Start();
        session.Stop();
        Assert.Equal(TorrentState.Stopped, session.State);
    }

    [Fact]
    public void Start_FromStopped_TransitionsToActive()
    {
        var session = CreateSession();
        session.Start();
        session.Stop();
        session.Start();
        Assert.Equal(TorrentState.Active, session.State);
    }

    [Fact]
    public void Pause_FromStopped_Throws()
    {
        var session = CreateSession();
        session.Stop();
        Assert.Throws<InvalidOperationException>(session.Pause);
    }

    [Fact]
    public void Start_WhileChecking_Throws()
    {
        var session = CreateSession();
        session.BeginChecking();
        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public void Pause_WhileChecking_Throws()
    {
        var session = CreateSession();
        session.BeginChecking();
        Assert.Throws<InvalidOperationException>(session.Pause);
    }

    [Fact]
    public void Stop_WhileChecking_Succeeds()
    {
        var session = CreateSession();
        session.BeginChecking();
        session.Stop();
        Assert.Equal(TorrentState.Stopped, session.State);
    }

    [Fact]
    public void FinishChecking_AllPiecesVerified_TransitionsToCompleted()
    {
        var session = CreateSession(pieceCount: 2);
        session.BeginChecking();
        session.MarkPieceVerified(0);
        session.MarkPieceVerified(1);
        session.FinishChecking();
        Assert.Equal(TorrentState.Completed, session.State);
    }

    [Fact]
    public void FinishChecking_NotAllPiecesVerified_TransitionsToPaused()
    {
        var session = CreateSession(pieceCount: 2);
        session.BeginChecking();
        session.MarkPieceVerified(0);
        session.FinishChecking();
        Assert.Equal(TorrentState.Paused, session.State);
    }

    [Fact]
    public void MarkPieceVerified_LastPieceWhileActive_TransitionsToSeeding()
    {
        var session = CreateSession(pieceCount: 2);
        session.Start();
        session.MarkPieceVerified(0);
        session.MarkPieceVerified(1);
        Assert.Equal(TorrentState.Seeding, session.State);
    }

    [Fact]
    public void Start_FromCompleted_TransitionsToSeeding()
    {
        var session = CreateSession(pieceCount: 2);
        session.BeginChecking();
        session.MarkPieceVerified(0);
        session.MarkPieceVerified(1);
        session.FinishChecking();
        Assert.Equal(TorrentState.Completed, session.State);

        session.Start();

        Assert.Equal(TorrentState.Seeding, session.State);
    }

    [Fact]
    public void Pause_FromSeeding_TransitionsToPaused()
    {
        var session = CreateSession(pieceCount: 1);
        session.Start();
        session.MarkPieceVerified(0);
        Assert.Equal(TorrentState.Seeding, session.State);

        session.Pause();

        Assert.Equal(TorrentState.Paused, session.State);
    }

    [Fact]
    public void Pause_FromCompleted_Throws()
    {
        var session = CreateSession(pieceCount: 1);
        session.BeginChecking();
        session.MarkPieceVerified(0);
        session.FinishChecking();
        Assert.Equal(TorrentState.Completed, session.State);

        Assert.Throws<InvalidOperationException>(session.Pause);
    }

    [Fact]
    public void Fail_SetsErrorStateAndMessage()
    {
        var session = CreateSession();
        session.Fail("peer wire error");
        Assert.Equal(TorrentState.Error, session.State);
        Assert.Equal("peer wire error", session.LastError);
    }

    [Fact]
    public void Start_FromError_RetriesToActiveAndClearsError()
    {
        var session = CreateSession();
        session.Fail("boom");
        session.Start();
        Assert.Equal(TorrentState.Active, session.State);
        Assert.Null(session.LastError);
    }

    [Fact]
    public void ProgressFraction_ReflectsVerifiedPieces()
    {
        var session = CreateSession(pieceCount: 4);
        session.MarkPieceVerified(0);
        session.MarkPieceVerified(1);
        Assert.Equal(0.5, session.ProgressFraction);
    }

    [Fact]
    public void SeedRatio_NothingDownloadedYet_IsNull()
    {
        var session = CreateSession();
        Assert.Null(session.SeedRatio);
    }

    [Fact]
    public void SeedRatio_ReflectsUploadedOverDownloaded()
    {
        var session = CreateSession();
        session.AddTransferredBytes(uploaded: 50, downloaded: 100);
        Assert.Equal(0.5, session.SeedRatio);
    }

    [Fact]
    public void AddTransferredBytes_Accumulates()
    {
        var session = CreateSession();
        session.AddTransferredBytes(10, 20);
        session.AddTransferredBytes(5, 5);
        Assert.Equal(15, session.TotalBytesUploaded);
        Assert.Equal(25, session.TotalBytesDownloaded);
    }

    [Fact]
    public void TotalSeedingElapsed_WhileSeeding_GrowsOverTime()
    {
        var session = CreateSession(pieceCount: 1);
        session.Start();
        session.MarkPieceVerified(0);
        Assert.Equal(TorrentState.Seeding, session.State);

        var first = session.TotalSeedingElapsed;
        Thread.Sleep(20);
        var second = session.TotalSeedingElapsed;

        Assert.True(second > first);
    }

    [Fact]
    public void TotalSeedingElapsed_BankedAcrossPauseAndResume()
    {
        var session = CreateSession(pieceCount: 1);
        session.Start();
        session.MarkPieceVerified(0);

        Thread.Sleep(20);
        session.Pause();
        var bankedAfterPause = session.TotalSeedingElapsed;

        // Not seeding right now - elapsed must not keep growing while paused.
        Thread.Sleep(20);
        Assert.Equal(bankedAfterPause, session.TotalSeedingElapsed);

        // Resuming continues accumulating on top of the banked time, not from zero.
        session.Start();
        Thread.Sleep(20);
        Assert.True(session.TotalSeedingElapsed > bankedAfterPause);
    }

    [Fact]
    public void StopSeedingDueToLimit_TransitionsToCompletedAndMarksLimitReached()
    {
        var session = CreateSession(pieceCount: 1);
        session.Start();
        session.MarkPieceVerified(0);

        session.StopSeedingDueToLimit();

        Assert.Equal(TorrentState.Completed, session.State);
        Assert.True(session.SeedingLimitReached);
    }

    [Fact]
    public void StopSeedingDueToLimit_FromNonSeedingState_Throws()
    {
        var session = CreateSession();
        Assert.Throws<InvalidOperationException>(session.StopSeedingDueToLimit);
    }

    [Fact]
    public void Start_AfterSeedingLimitReached_ResumesSeedingWithFlagStillSet()
    {
        // Per the intended UX: once the seed-until policy has stopped a torrent, manually
        // starting it again means "seed forever" - the flag deliberately never resets
        // itself back to false.
        var session = CreateSession(pieceCount: 1);
        session.Start();
        session.MarkPieceVerified(0);
        session.StopSeedingDueToLimit();

        session.Start();

        Assert.Equal(TorrentState.Seeding, session.State);
        Assert.True(session.SeedingLimitReached);
    }

    [Fact]
    public void Constructor_RejectsEmptyDownloadDirectory()
    {
        Assert.Throws<ArgumentException>(() =>
            new TorrentSession(
                TorrentAddSource.Magnet.FromInfoHash("0123456789abcdef0123456789abcdef01234567"),
                MagnetLinkMetadataWithPieces(1),
                " "));
    }
}
