using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Aggregates tracker announces, DHT, and LAN discovery into a single peer feed for one torrent.</summary>
public interface IPeerSource : IDisposable
{
    event Action<IPEndPoint> PeerFound;

    /// <summary>Peers reported by tracker announces so far (raw count, not deduped).</summary>
    int TrackerPeersFound { get; }

    /// <summary>Peers reported by DHT so far (raw count, not deduped).</summary>
    int DhtPeersFound { get; }

    /// <summary>Peers reported by LAN discovery (BEP-14) so far (raw count, not deduped).</summary>
    int LanPeersFound { get; }

    /// <summary>Size of this torrent's own DHT routing table — 0 if DHT is disabled (private torrent) or not yet bootstrapped.</summary>
    int DhtNodeCount { get; }

    void Start();
    void Stop();
}
