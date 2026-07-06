namespace bzTorrentClient.Engine.Sessions;

/// <summary>A snapshot of one torrent's live network activity, for surfacing in the UI.</summary>
public sealed record TorrentNetworkStats(
    int ActiveConnections,
    long BytesDownloaded,
    long BytesUploaded,
    int TrackerPeersFound,
    int DhtPeersFound,
    int LanPeersFound,
    int PexPeersFound,
    int DhtNodeCount)
{
    public static readonly TorrentNetworkStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
}
