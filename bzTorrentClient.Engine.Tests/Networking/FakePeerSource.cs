using System.Net;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Tests.Networking;

internal sealed class FakePeerSource : IPeerSource
{
    private Action<IPEndPoint>? _peerFound;

    /// <summary>How many times something has subscribed to <see cref="PeerFound"/> — each call to MetadataFetcher.TryFetchAsync subscribes exactly once, so this doubles as an attempt counter.</summary>
    public int SubscribeCount { get; private set; }

    public event Action<IPEndPoint>? PeerFound
    {
        add { _peerFound += value; SubscribeCount++; }
        remove => _peerFound -= value;
    }

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public int TrackerPeersFound { get; set; }
    public int DhtPeersFound { get; set; }
    public int LanPeersFound { get; set; }
    public int DhtNodeCount { get; set; }
    public IReadOnlyCollection<TrackerStatus> TrackerStatuses { get; set; } = Array.Empty<TrackerStatus>();

    public void Start() => Started = true;
    public void Stop() => Stopped = true;
    public void Dispose() => Disposed = true;

    public void Raise(IPEndPoint endpoint) => _peerFound?.Invoke(endpoint);
}
