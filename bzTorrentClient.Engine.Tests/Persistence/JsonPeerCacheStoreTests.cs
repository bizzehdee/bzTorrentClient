using System.Net;
using bzTorrentClient.Engine.Persistence;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Persistence;

public class JsonPeerCacheStoreTests : IDisposable
{
    private const string InfoHash = "0123456789ABCDEF0123456789abcdef01234567";
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bzt-peercache-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPeers_AcrossStoreInstances()
    {
        var peers = new[]
        {
            new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881),
            new IPEndPoint(IPAddress.Parse("10.0.0.2"), 51413),
        };

        new JsonPeerCacheStore(_filePath).Save(InfoHash, peers);

        // A fresh instance reads the file back - simulating a restart.
        var reloaded = new JsonPeerCacheStore(_filePath).Load(InfoHash);

        Assert.Equal(peers, reloaded);
    }

    [Fact]
    public void Load_IsCaseInsensitiveOnInfoHash()
    {
        var peer = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 6881);
        var store = new JsonPeerCacheStore(_filePath);
        store.Save(InfoHash.ToUpperInvariant(), new[] { peer });

        Assert.Equal(new[] { peer }, store.Load(InfoHash.ToLowerInvariant()));
    }

    [Fact]
    public void Save_EmptySet_RemovesEntryRatherThanPersistingEmpty()
    {
        var store = new JsonPeerCacheStore(_filePath);
        store.Save(InfoHash, new[] { new IPEndPoint(IPAddress.Loopback, 6881) });

        store.Save(InfoHash, Array.Empty<IPEndPoint>());

        Assert.Empty(store.Load(InfoHash));
        Assert.Empty(new JsonPeerCacheStore(_filePath).Load(InfoHash));
    }

    [Fact]
    public void Load_UnknownInfoHash_ReturnsEmpty()
    {
        var store = new JsonPeerCacheStore(_filePath);
        Assert.Empty(store.Load("ffffffffffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new JsonPeerCacheStore(Path.Combine(Path.GetTempPath(), $"bzt-missing-{Guid.NewGuid():N}.json"));
        Assert.Empty(store.Load(InfoHash));
    }
}
