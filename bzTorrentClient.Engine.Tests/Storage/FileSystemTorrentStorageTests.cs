using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Testing;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Storage;

public class FileSystemTorrentStorageTests : IDisposable
{
    private readonly string _tempDir;

    public FileSystemTorrentStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static FakeMetadata SingleFileMetadata(long fileSize, long pieceSize) => new(
        pieceCount: (int)Math.Ceiling(fileSize / (double)pieceSize),
        pieceSize: pieceSize,
        files: new[] { new MetadataFileInfo { Id = 0, Filename = "single.bin", FileStartByte = 0, FileSize = fileSize } });

    private static FakeMetadata MultiFileMetadata(long pieceSize) => new(
        pieceCount: 2,
        pieceSize: pieceSize,
        files: new[]
        {
            new MetadataFileInfo { Id = 0, Filename = "a.bin", FileStartByte = 0, FileSize = 10 },
            new MetadataFileInfo { Id = 1, Filename = "b.bin", FileStartByte = 10, FileSize = 20 },
        });

    [Fact]
    public void EnsureAllocated_CreatesFilesAtDeclaredSize()
    {
        var metadata = SingleFileMetadata(fileSize: 100, pieceSize: 16384);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);

        storage.EnsureAllocated();

        var path = Path.Combine(_tempDir, "single.bin");
        Assert.True(File.Exists(path));
        Assert.Equal(100, new FileInfo(path).Length);
    }

    [Fact]
    public void GetPieceLength_LastPieceIsRemainder()
    {
        var metadata = SingleFileMetadata(fileSize: 100, pieceSize: 64);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);

        Assert.Equal(64, storage.GetPieceLength(0));
        Assert.Equal(36, storage.GetPieceLength(1));
    }

    [Fact]
    public void WriteBlockThenReadPiece_RoundTrips()
    {
        var metadata = SingleFileMetadata(fileSize: 32, pieceSize: 32);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        storage.WriteBlock(0, 0, data);

        var readBack = storage.ReadPiece(0);
        Assert.Equal(data, readBack);
    }

    [Fact]
    public void WriteBlock_SpanningTwoFiles_WritesToBoth()
    {
        var metadata = MultiFileMetadata(pieceSize: 30);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        // Piece 0 covers bytes [0, 30) of the concatenated stream: all of a.bin (10 bytes)
        // and the first 20 bytes of b.bin.
        var block = Enumerable.Range(1, 30).Select(i => (byte)i).ToArray();
        storage.WriteBlock(0, 0, block);

        var aBytes = File.ReadAllBytes(Path.Combine(_tempDir, "a.bin"));
        var bBytes = File.ReadAllBytes(Path.Combine(_tempDir, "b.bin"));

        Assert.Equal(block[..10], aBytes);
        Assert.Equal(block[10..], bBytes[..20]);
    }

    [Fact]
    public void ReadPiece_SpanningTwoFiles_ReassemblesCorrectly()
    {
        var metadata = MultiFileMetadata(pieceSize: 30);
        var storage = new FileSystemTorrentStorage(metadata, _tempDir);
        storage.EnsureAllocated();

        var block = Enumerable.Range(1, 30).Select(i => (byte)i).ToArray();
        storage.WriteBlock(0, 0, block);

        var piece = storage.ReadPiece(0);
        Assert.Equal(block, piece);
    }
}
