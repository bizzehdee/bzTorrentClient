using bzBencode;
using bzTorrent.Data;
using bzTorrentClient.Engine.Networking;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

public class MetadataFetcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-metadatafetcher-{Guid.NewGuid():N}");

    public MetadataFetcherTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadFetchedMetadata_MagnetSessionReceivingInfoDict_CanSubsequentlyBeSaved()
    {
        // Regression test: a magnet-sourced Metadata's internal bencoded root is only ever
        // set by Load(Stream); calling Metadata.LoadInfoDictionary directly (as BEP-9
        // fetch used to) leaves it null, so a later Save() - needed to cache the fetched
        // metadata against the torrent's info-hash - throws and (since the caller swallows
        // it) silently discards the entire fetch. LoadFetchedMetadata must avoid that.
        var infoDict = BuildRealInfoDict(out var expectedFilename);

        var magnetMetadata = new Metadata();
        magnetMetadata.Load(MagnetLink.Resolve($"magnet:?xt=urn:btih:{new string('a', 40)}"));

        Assert.True(MetadataFetcher.LoadFetchedMetadata(magnetMetadata, infoDict));

        using var saved = new MemoryStream();
        var exception = Record.Exception(() => magnetMetadata.Save(saved));

        Assert.Null(exception);
        Assert.NotEmpty(magnetMetadata.GetFileInfos());
        Assert.Equal(expectedFilename, magnetMetadata.GetFileInfos().Single().Filename);
        Assert.NotEmpty(magnetMetadata.PieceHashes);
    }

    [Fact]
    public void LoadFetchedMetadata_SavedThenReloaded_RoundTripsFileInfo()
    {
        // The whole point: once cached, a restart must reconstruct the same file/piece
        // info from the saved bytes alone, with no further BEP-9 fetch involved.
        var infoDict = BuildRealInfoDict(out var expectedFilename);

        var magnetMetadata = new Metadata();
        magnetMetadata.Load(MagnetLink.Resolve($"magnet:?xt=urn:btih:{new string('b', 40)}"));
        MetadataFetcher.LoadFetchedMetadata(magnetMetadata, infoDict);

        using var saved = new MemoryStream();
        magnetMetadata.Save(saved);

        var reloaded = Metadata.FromBuffer(saved.ToArray());

        Assert.Equal(expectedFilename, reloaded.GetFileInfos().Single().Filename);
        Assert.Equal(magnetMetadata.PieceHashes.Count, reloaded.PieceHashes.Count);
    }

    private BDict BuildRealInfoDict(out string filename)
    {
        filename = "content.bin";
        var sourceFile = Path.Combine(_tempDir, filename);
        File.WriteAllBytes(sourceFile, Enumerable.Range(0, 100).Select(b => (byte)b).ToArray());

        var built = Metadata.CreateFromPath(sourceFile);
        using var builtBytes = new MemoryStream();
        built.Save(builtBytes);

        var rootDict = (BDict)BencodingUtils.Decode(new MemoryStream(builtBytes.ToArray()));
        return (BDict)rootDict["info"];
    }
}
