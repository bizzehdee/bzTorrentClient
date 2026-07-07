using System.Net;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// Remembers which peers a torrent was connected to, keyed by info-hash, so a restart can
/// re-seed the swarm from known-good peers immediately instead of waiting on a fresh
/// tracker/DHT cycle. Best-effort - stale peers just fail to connect and get dropped.
/// </summary>
public interface IPeerCacheStore
{
    /// <summary>The last-known peers for this torrent (empty if none cached).</summary>
    IReadOnlyList<IPEndPoint> Load(string infoHash);

    /// <summary>Replaces the cached peer set for this torrent.</summary>
    void Save(string infoHash, IReadOnlyCollection<IPEndPoint> peers);
}
