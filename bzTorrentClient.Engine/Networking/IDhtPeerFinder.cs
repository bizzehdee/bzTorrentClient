using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Thin seam over bzTorrent's <see cref="bzTorrent.DHT.DHTClient"/> so peer discovery can be faked in tests without opening a real UDP socket.</summary>
public interface IDhtPeerFinder : IDisposable
{
    event Action<IPEndPoint> PeerFound;

    /// <summary>Size of this node's DHT routing table — 0 until bootstrap has found at least one node.</summary>
    int NodeCount { get; }

    void StartSearch(byte[] infoHash);
}
