using System.Net;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Tests.Networking;

internal sealed class FakePeerSource : IPeerSource
{
    public event Action<IPEndPoint>? PeerFound;

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public int TrackerPeersFound { get; set; }
    public int DhtPeersFound { get; set; }
    public int LanPeersFound { get; set; }
    public int DhtNodeCount { get; set; }

    public void Start() => Started = true;
    public void Stop() => Stopped = true;
    public void Dispose() => Disposed = true;

    public void Raise(IPEndPoint endpoint) => PeerFound?.Invoke(endpoint);
}
