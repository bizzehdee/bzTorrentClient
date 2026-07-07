using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Sessions;

/// <summary>
/// Optional richer read-only view into a running torrent's live network state (peer
/// count, connected endpoints), implemented by <see cref="NetworkedSessionManager"/>.
/// Kept separate from <see cref="ISessionManager"/> so that interface isn't polluted
/// with networking-specific concerns a plain (non-networked) session manager can't answer.
/// </summary>
public interface ITorrentRuntimeInfoProvider
{
    int GetActiveConnectionCount(Guid sessionId);

    IReadOnlyCollection<PeerConnectionInfo> GetConnectedPeers(Guid sessionId);

    /// <summary>Returns <see cref="TorrentNetworkStats.Empty"/> if the torrent has no active runtime (never started, or stopped).</summary>
    TorrentNetworkStats GetNetworkStats(Guid sessionId);
}
