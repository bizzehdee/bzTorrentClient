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
    public void TryGetNextRequest_PrefersRarerPiece()
    {
        var (metadata, storage, _) = CreateFixture(2);
        var manager = new RarestFirstPieceManager(metadata, storage);

        // Piece 0 is available from two peers, piece 1 from only one -> piece 1 is rarer.
        manager.RegisterPeerBitfield(1, new[] { true, false });
        manager.RegisterPeerBitfield(2, new[] { true, true });

        var request = manager.TryGetNextRequest(2, new[] { true, true });

        Assert.NotNull(request);
        Assert.Equal(1, request!.PieceIndex);
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
