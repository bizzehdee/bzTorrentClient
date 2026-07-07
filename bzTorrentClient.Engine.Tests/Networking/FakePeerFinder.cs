using System.Net;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Tests.Networking;

/// <summary>Fake standing in for both <see cref="IDhtPeerFinder"/> and <see cref="ILanPeerFinder"/> so tests never open a real socket.</summary>
internal sealed class FakePeerFinder : IDhtPeerFinder, ILanPeerFinder
{
    public event Action<IPEndPoint>? PeerFound;

    public bool Disposed { get; private set; }
    public byte[]? SearchedInfoHash { get; private set; }
    public (int Port, string InfoHashHex)? Announced { get; private set; }
    public int NodeCount { get; set; }

    public List<DhtNodeInfo> SeededNodes { get; } = new();
    public IReadOnlyList<DhtNodeInfo> NodesToReturn { get; set; } = Array.Empty<DhtNodeInfo>();

    public void StartSearch(byte[] infoHash) => SearchedInfoHash = infoHash;

    public IReadOnlyList<DhtNodeInfo> GetNodes() => NodesToReturn;

    public void SeedNodes(IEnumerable<DhtNodeInfo> nodes) => SeededNodes.AddRange(nodes);

    public void Announce(int listenPort, string infoHashHex) => Announced = (listenPort, infoHashHex);

    public void Raise(IPEndPoint endpoint) => PeerFound?.Invoke(endpoint);

    public void Dispose() => Disposed = true;
}
