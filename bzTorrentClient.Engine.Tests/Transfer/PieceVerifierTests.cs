using System.Security.Cryptography;
using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Testing;
using bzTorrentClient.Engine.Transfer;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Transfer;

public class PieceVerifierTests : IDisposable
{
    private const long PieceSize = 16;
    private readonly string _tempDir;

    public PieceVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private (FakeMetadata metadata, FileSystemTorrentStorage storage) BuildTorrent(params byte[][] pieceContents)
    {
        var totalSize = pieceContents.Sum(p => (long)p.Length);
        var files = new[] { new MetadataFileInfo { Id = 0, Filename = "single.bin", FileStartByte = 0, FileSize = totalSize } };
        var hashes = pieceContents.Select(SHA1.HashData).ToList<byte[]>();
        var metadata = new FakeMetadata(pieceContents.Length, pieceSize: PieceSize, pieceHashes: hashes, files: files);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var path = Path.Combine(_tempDir, "single.bin");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            foreach (var piece in pieceContents)
                stream.Write(piece, 0, piece.Length);
        }

        return (metadata, storage);
    }

    [Fact]
    public void Verify_AllPiecesMatchOnDisk_MarksEveryPieceComplete()
    {
        var (metadata, storage) = BuildTorrent(
            Enumerable.Repeat((byte)1, (int)PieceSize).ToArray(),
            Enumerable.Repeat((byte)2, (int)PieceSize).ToArray());

        var completion = PieceVerifier.Verify(metadata, storage);

        Assert.Equal(new[] { true, true }, completion);
    }

    [Fact]
    public void Verify_MissingFile_MarksEveryPieceIncomplete()
    {
        var totalSize = PieceSize * 2;
        var files = new[] { new MetadataFileInfo { Id = 0, Filename = "single.bin", FileStartByte = 0, FileSize = totalSize } };
        var hashes = new List<byte[]>
        {
            SHA1.HashData(Enumerable.Repeat((byte)1, (int)PieceSize).ToArray()),
            SHA1.HashData(Enumerable.Repeat((byte)2, (int)PieceSize).ToArray()),
        };
        var metadata = new FakeMetadata(2, pieceSize: PieceSize, pieceHashes: hashes, files: files);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);

        var completion = PieceVerifier.Verify(metadata, storage);

        Assert.Equal(new[] { false, false }, completion);
    }

    [Fact]
    public void Verify_OneCorruptedPiece_OnlyMarksThatPieceIncomplete()
    {
        var (metadata, storage) = BuildTorrent(
            Enumerable.Repeat((byte)1, (int)PieceSize).ToArray(),
            Enumerable.Repeat((byte)2, (int)PieceSize).ToArray());

        // Overwrite the second piece on disk so it no longer matches its recorded hash.
        var path = Path.Combine(_tempDir, "single.bin");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(PieceSize, SeekOrigin.Begin);
            stream.Write(Enumerable.Repeat((byte)0xFF, (int)PieceSize).ToArray());
        }

        var completion = PieceVerifier.Verify(metadata, storage);

        Assert.Equal(new[] { true, false }, completion);
    }
}
