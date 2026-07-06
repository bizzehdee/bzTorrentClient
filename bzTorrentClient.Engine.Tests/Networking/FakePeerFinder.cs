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

    public void StartSearch(byte[] infoHash) => SearchedInfoHash = infoHash;

    public void Announce(int listenPort, string infoHashHex) => Announced = (listenPort, infoHashHex);

    public void Raise(IPEndPoint endpoint) => PeerFound?.Invoke(endpoint);

    public void Dispose() => Disposed = true;
}
