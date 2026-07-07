using System.Net;
using bzTorrent;

namespace bzTorrentClient.Engine.Networking;

public interface IPeerConnectionManager : IDisposable
{
    int ActiveConnectionCount { get; }

    IReadOnlyCollection<PeerConnectionInfo> ConnectedPeers { get; }

    /// <summary>Peers learned about via Peer Exchange (BEP-11) so far (raw count, not deduped).</summary>
    int PexPeersFound { get; }

    /// <summary>Total raw bytes received in Piece messages, including from pieces that later failed hash verification.</summary>
    long BytesDownloaded { get; }

    /// <summary>Total bytes sent in response to peer Requests.</summary>
    long BytesUploaded { get; }

    void AddPeerCandidate(IPEndPoint endpoint);

    /// <summary>
    /// Raised when a peer is learned about from a connected peer via PEX (BEP-11). Lets the
    /// owner feed those peers back into the shared peer source so other consumers - notably
    /// the metadata fetcher - benefit from the swarm PEX uncovers, not just tracker/DHT/LAN.
    /// </summary>
    event Action<IPEndPoint>? PeerDiscovered;

    /// <summary>
    /// Adopts an inbound peer connection whose handshake has already been read (so the
    /// listener could route it to this torrent). Sends our handshake and then serves/leeches
    /// over the shared peer loop. Runs inline until the connection ends.
    /// </summary>
    void AcceptInbound(IPeerWireClient client, IPEndPoint remoteEndpoint);

    void Start();

    /// <summary>Soft halt: disconnects active peers but keeps the known-peer/candidate queue, so a later <see cref="Start"/> reconnects without waiting for fresh discovery.</summary>
    void Pause();

    /// <summary>Hard halt: disconnects active peers and forgets every known/candidate peer.</summary>
    void Stop();
}
