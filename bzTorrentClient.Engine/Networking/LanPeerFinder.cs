using System.Net;
using bzTorrent;
using bzTorrent.IO;

namespace bzTorrentClient.Engine.Networking;

public sealed class LanPeerFinder : ILanPeerFinder
{
    private readonly LocalPeerDiscovery<UDPSocket> _lpd = new();

    public event Action<IPEndPoint>? PeerFound;

    public LanPeerFinder()
    {
        _lpd.NewPeer += (address, port, _) => PeerFound?.Invoke(new IPEndPoint(address, port));
        _lpd.Open();
    }

    public void Announce(int listenPort, string infoHashHex) => _lpd.Announce(listenPort, infoHashHex);

    public void Dispose() => _lpd.Dispose();
}
