using System.Net;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Persistence;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Persistence;

public class JsonDhtNodeStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bzt-dhtnodes-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsNodes_AcrossStoreInstances()
    {
        var nodes = new[]
        {
            new DhtNodeInfo(new byte[] { 1, 2, 3, 4 }, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881)),
            new DhtNodeInfo(new byte[] { 0xAB, 0xCD }, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 51413)),
        };

        new JsonDhtNodeStore(_filePath).Save(nodes);

        var reloaded = new JsonDhtNodeStore(_filePath).Load();

        Assert.Equal(2, reloaded.Count);
        for (var i = 0; i < nodes.Length; i++)
        {
            Assert.Equal(nodes[i].Id, reloaded[i].Id);
            Assert.Equal(nodes[i].EndPoint, reloaded[i].EndPoint);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new JsonDhtNodeStore(_filePath).Load());
    }

    [Fact]
    public void Load_SkipsCorruptEntries()
    {
        File.WriteAllText(_filePath, "[{\"Id\":\"zz\",\"Ip\":\"10.0.0.1\",\"Port\":6881},{\"Id\":\"01\",\"Ip\":\"nonsense\",\"Port\":6881},{\"Id\":\"02\",\"Ip\":\"10.0.0.3\",\"Port\":6881}]");

        var loaded = new JsonDhtNodeStore(_filePath).Load();

        var node = Assert.Single(loaded);
        Assert.Equal(new byte[] { 2 }, node.Id);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.3"), 6881), node.EndPoint);
    }
}
