namespace bzTorrentClient.Engine.Networking;

/// <summary>Live per-tracker announce state for one torrent, as last reported by that tracker.</summary>
public sealed record TrackerStatus(
    string Url,
    int PeersFound,
    int Seeders,
    int Leechers,
    DateTime? LastAnnounceUtc,
    string? LastError);
