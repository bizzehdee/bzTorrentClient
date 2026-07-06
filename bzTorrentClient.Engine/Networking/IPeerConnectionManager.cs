using System.Net;

namespace bzTorrentClient.Engine.Networking;

public interface IPeerConnectionManager : IDisposable
{
    int ActiveConnectionCount { get; }

    IReadOnlyCollection<IPEndPoint> ConnectedEndpoints { get; }

    /// <summary>Peers learned about via Peer Exchange (BEP-11) so far (raw count, not deduped).</summary>
    int PexPeersFound { get; }

    /// <summary>Total raw bytes received in Piece messages, including from pieces that later failed hash verification.</summary>
    long BytesDownloaded { get; }

    /// <summary>Total bytes sent in response to peer Requests.</summary>
    long BytesUploaded { get; }

    void AddPeerCandidate(IPEndPoint endpoint);

    void Start();

    /// <summary>Soft halt: disconnects active peers but keeps the known-peer/candidate queue, so a later <see cref="Start"/> reconnects without waiting for fresh discovery.</summary>
    void Pause();

    /// <summary>Hard halt: disconnects active peers and forgets every known/candidate peer.</summary>
    void Stop();
}
