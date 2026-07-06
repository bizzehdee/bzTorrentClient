using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Thin seam over bzTorrent's <see cref="bzTorrent.LocalPeerDiscovery{T}"/> (BEP-14) so it can be faked in tests without opening a real multicast socket.</summary>
public interface ILanPeerFinder : IDisposable
{
    event Action<IPEndPoint> PeerFound;

    void Announce(int listenPort, string infoHashHex);
}
