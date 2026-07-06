using bzTorrentClient.Engine.Sessions;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class TorrentAddSourceTests
{
    private const string InfoHashHex = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Magnet_ResolveMetadata_ExposesHashAndNoPieces()
    {
        var source = new TorrentAddSource.Magnet($"magnet:?xt=urn:btih:{InfoHashHex}&dn=example");
        var metadata = source.ResolveMetadata();

        Assert.Equal(InfoHashHex, metadata.HashString.ToLowerInvariant());
        Assert.Empty(metadata.PieceHashes);
    }

    [Fact]
    public void FromInfoHash_BuildsTrackerlessMagnetUri()
    {
        var source = TorrentAddSource.Magnet.FromInfoHash(InfoHashHex);
        Assert.Equal($"magnet:?xt=urn:btih:{InfoHashHex}", source.Uri);
    }

    [Fact]
    public void FromInfoHash_ResolvesToMetadataWithMatchingHash()
    {
        var source = TorrentAddSource.Magnet.FromInfoHash(InfoHashHex);
        var metadata = source.ResolveMetadata();
        Assert.Equal(InfoHashHex, metadata.HashString.ToLowerInvariant());
    }

    [Fact]
    public void Magnet_RejectsEmptyUri()
    {
        Assert.Throws<ArgumentException>(() => new TorrentAddSource.Magnet(""));
    }

    [Fact]
    public void FromInfoHash_RejectsEmptyHash()
    {
        Assert.Throws<ArgumentException>(() => TorrentAddSource.Magnet.FromInfoHash(""));
    }

    [Fact]
    public void TorrentFile_RejectsNullBytes()
    {
        Assert.Throws<ArgumentNullException>(() => new TorrentAddSource.TorrentFile(null!));
    }

    [Fact]
    public void TorrentFile_ResolveMetadata_ParsesRealTorrentFile()
    {
        var bytes = File.ReadAllBytes(Path.Combine("TestFiles", "UbuntuTestTorrent.torrent"));
        var source = new TorrentAddSource.TorrentFile(bytes);
        var metadata = source.ResolveMetadata();

        Assert.NotEmpty(metadata.PieceHashes);
        Assert.False(string.IsNullOrEmpty(metadata.Name));
    }
}
