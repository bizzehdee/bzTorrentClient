using System.Security.Cryptography;
using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Testing;
using bzTorrentClient.Engine.Transfer;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Transfer;

public class RarestFirstPieceManagerTests : IDisposable
{
    private const int PieceSize = 16;
    private readonly string _tempDir;

    public RarestFirstPieceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-pieces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private (FakeMetadata metadata, FileSystemTorrentStorage storage, byte[][] pieceContents) CreateFixture(int pieceCount)
    {
        var contents = new byte[pieceCount][];
        var hashes = new byte[pieceCount][];
        for (var i = 0; i < pieceCount; i++)
        {
            contents[i] = Enumerable.Range(0, PieceSize).Select(b => (byte)(b + i)).ToArray();
            hashes[i] = SHA1.HashData(contents[i]);
        }

        var metadata = new FakeMetadata(
            pieceCount,
            pieceSize: PieceSize,
            pieceHashes: hashes,
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "file.bin", FileStartByte = 0, FileSize = pieceCount * PieceSize } });

        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        return (metadata, storage, contents);
    }

    [Fact]
    public void TryGetNextRequest_NoPeerHasPiece_ReturnsNull()
    {
        var (metadata, storage, _) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        var request = manager.TryGetNextRequest(1, new[] { false });

        Assert.Null(request);
    }

    [Fact]
    public void TryGetNextRequest_DuringRampUp_PrefersMostAvailablePiece()
    {
        // Fewer than RampUpPieceGoal pieces are complete (zero, here), so picking favors
        // whatever's most available - fast, easy data from many peers to get things moving.
        var (metadata, storage, _) = CreateFixture(2);
        var manager = new RarestFirstPieceManager(metadata, storage);

        // Piece 0 is available from two peers, piece 1 from only one -> piece 0 is more available.
        manager.RegisterPeerBitfield(1, new[] { true, false });
        manager.RegisterPeerBitfield(2, new[] { true, true });

        var request = manager.TryGetNextRequest(2, new[] { true, true });

        Assert.NotNull(request);
        Assert.Equal(0, request!.PieceIndex);
    }

    [Fact]
    public void TryGetNextRequest_AfterRampUp_PrefersRarerPiece()
    {
        // Once RampUpPieceGoal pieces are complete, picking switches to rarest-first for
        // swarm health - a scarce piece isn't left to disappear if its few holders leave.
        const int rampUpGoal = 4;
        var (metadata, storage, contents) = CreateFixture(rampUpGoal + 2);
        var manager = new RarestFirstPieceManager(metadata, storage);

        for (var i = 0; i < rampUpGoal; i++)
            manager.OnBlockReceived(i, 0, contents[i]);

        var lastTwo = new bool[rampUpGoal + 2];
        lastTwo[rampUpGoal] = true;
        lastTwo[rampUpGoal + 1] = true;

        // Both remaining pieces are available from peer 1, but only the last one is also
        // available from peer 2 - so the second-to-last piece (index rampUpGoal) is rarer.
        manager.RegisterPeerBitfield(1, lastTwo);
        var onlyLastPiece = new bool[rampUpGoal + 2];
        onlyLastPiece[rampUpGoal + 1] = true;
        manager.RegisterPeerBitfield(2, onlyLastPiece);

        var request = manager.TryGetNextRequest(1, lastTwo);

        Assert.NotNull(request);
        Assert.Equal(rampUpGoal, request!.PieceIndex);
    }

    [Fact]
    public void OnBlockReceived_CorrectHash_CompletesPieceAndPersists()
    {
        var (metadata, storage, contents) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        var completedPiece = manager.OnBlockReceived(0, 0, contents[0]);

        Assert.Equal(0, completedPiece);
        Assert.True(manager.IsPieceComplete(0));
        Assert.True(manager.IsComplete);
    }

    [Fact]
    public void OnBlockReceived_CorrectHash_RaisesPieceCompleted()
    {
        // Regression test: nothing outside this manager was told when a piece finished —
        // NetworkedSessionManager had no way to update TorrentSession.PieceCompletion, so
        // download progress/"downloaded" bytes stayed stuck at 0% even while data was
        // genuinely being received and written to disk. PieceCompleted is how that gap
        // gets closed: NetworkedSessionManager wires it straight to session.MarkPieceVerified.
        var (metadata, storage, contents) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        int? raisedIndex = null;
        manager.PieceCompleted += index => raisedIndex = index;

        manager.OnBlockReceived(0, 0, contents[0]);

        Assert.Equal(0, raisedIndex);
    }

    [Fact]
    public void OnBlockReceived_WrongHash_DoesNotRaisePieceCompleted()
    {
        var (metadata, storage, _) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        var raised = false;
        manager.PieceCompleted += _ => raised = true;

        manager.OnBlockReceived(0, 0, new byte[PieceSize]);

        Assert.False(raised);
    }

    [Fact]
    public void OnBlockReceived_WrongHash_DoesNotCompleteAndAllowsRetry()
    {
        var (metadata, storage, _) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        var badData = new byte[PieceSize];
        var result = manager.OnBlockReceived(0, 0, badData);

        Assert.Null(result);
        Assert.False(manager.IsPieceComplete(0));

        // The piece should be requestable again from scratch.
        manager.RegisterPeerBitfield(1, new[] { true });
        var request = manager.TryGetNextRequest(1, new[] { true });
        Assert.NotNull(request);
        Assert.Equal(0, request!.BlockOffset);
    }

    [Fact]
    public void TryGetNextRequest_InProgressPiece_CanBeSuppliedByMultiplePeers()
    {
        // A piece isn't reserved for whichever peer started it - once one peer's block
        // request leaves it in-progress, a different peer that also has it should be able
        // to supply its other blocks too.
        var pieceSize = RarestFirstPieceManager.BlockSize * 2;
        var content = new byte[pieceSize];
        var hash = SHA1.HashData(content);
        var metadata = new FakeMetadata(
            1,
            pieceSize: pieceSize,
            pieceHashes: new[] { hash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "file.bin", FileStartByte = 0, FileSize = pieceSize } });
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var manager = new RarestFirstPieceManager(metadata, storage);
        manager.RegisterPeerBitfield(1, new[] { true });
        manager.RegisterPeerBitfield(2, new[] { true });

        var fromPeer1 = manager.TryGetNextRequest(1, new[] { true });
        var fromPeer2 = manager.TryGetNextRequest(2, new[] { true });

        Assert.NotNull(fromPeer1);
        Assert.NotNull(fromPeer2);
        Assert.Equal(0, fromPeer1!.PieceIndex);
        Assert.Equal(0, fromPeer2!.PieceIndex);
        Assert.NotEqual(fromPeer1.BlockOffset, fromPeer2.BlockOffset);
    }

    [Fact]
    public void UnregisterPeer_ReleasesDroppedPeersOutstandingBlocks_SoAnotherPeerFinishesThePiece()
    {
        // Regression: a peer requests blocks of a piece then drops before delivering them all.
        // The undelivered blocks must be re-offered to another peer, or the partially-
        // downloaded piece stalls forever.
        var pieceSize = RarestFirstPieceManager.BlockSize * 2;
        var content = new byte[pieceSize];
        var hash = SHA1.HashData(content);
        var metadata = new FakeMetadata(
            1,
            pieceSize: pieceSize,
            pieceHashes: new[] { hash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "file.bin", FileStartByte = 0, FileSize = pieceSize } });
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var manager = new RarestFirstPieceManager(metadata, storage);
        manager.RegisterPeerBitfield(1, new[] { true });
        manager.RegisterPeerBitfield(2, new[] { true });

        // Peer 1 takes both blocks of the piece...
        var block1 = manager.TryGetNextRequest(1, new[] { true });
        var block2 = manager.TryGetNextRequest(1, new[] { true });
        Assert.NotNull(block1);
        Assert.NotNull(block2);

        // ...delivers one, then drops before delivering the other.
        manager.OnBlockReceived(0, block1!.BlockOffset, new byte[block1.Length]);

        // While peer 1 still "holds" the second block, peer 2 has nothing left to request.
        Assert.Null(manager.TryGetNextRequest(2, new[] { true }));

        manager.UnregisterPeer(1);

        // Now the abandoned (undelivered) block is re-offered to peer 2 - the piece can finish.
        var reoffered = manager.TryGetNextRequest(2, new[] { true });
        Assert.NotNull(reoffered);
        Assert.Equal(block2!.BlockOffset, reoffered!.BlockOffset);

        // The block peer 1 did deliver is not re-requested.
        Assert.Null(manager.TryGetNextRequest(2, new[] { true }));
    }

    [Fact]
    public void TryGetNextRequest_DoesNotReRequestAlreadyRequestedBlock()
    {
        // A piece spanning two blocks, so the second TryGetNextRequest call must move
        // on to the second block rather than re-offering the first.
        var pieceSize = RarestFirstPieceManager.BlockSize * 2;
        var content = new byte[pieceSize];
        var hash = SHA1.HashData(content);
        var metadata = new FakeMetadata(
            1,
            pieceSize: pieceSize,
            pieceHashes: new[] { hash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "file.bin", FileStartByte = 0, FileSize = pieceSize } });
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var manager = new RarestFirstPieceManager(metadata, storage);
        manager.RegisterPeerBitfield(1, new[] { true });

        var first = manager.TryGetNextRequest(1, new[] { true });
        var second = manager.TryGetNextRequest(1, new[] { true });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.BlockOffset, second!.BlockOffset);
    }

    [Fact]
    public void UnregisterPeer_DecrementsAvailability()
    {
        var (metadata, storage, _) = CreateFixture(1);
        var manager = new RarestFirstPieceManager(metadata, storage);

        manager.RegisterPeerBitfield(1, new[] { true });
        manager.UnregisterPeer(1);

        var request = manager.TryGetNextRequest(2, new[] { true });
        Assert.Null(request);
    }

    [Fact]
    public void IsComplete_FalseWhenNoPiecesYetKnown()
    {
        var (metadata, storage, _) = CreateFixture(0);
        var manager = new RarestFirstPieceManager(metadata, storage);

        Assert.False(manager.IsComplete);
    }
}
