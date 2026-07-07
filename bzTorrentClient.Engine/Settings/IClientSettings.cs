namespace bzTorrentClient.Engine.Settings;

public interface IClientSettings
{
    string DefaultDownloadDirectory { get; set; }
    int GlobalMaxConnections { get; set; }
    int MaxConnectionsPerTorrent { get; set; }
    int ListenPort { get; set; }

    /// <summary>Global download throughput cap in bytes/second, shared across every torrent. Zero or less means unlimited.</summary>
    long GlobalDownloadLimitBytesPerSecond { get; set; }

    /// <summary>Global upload throughput cap in bytes/second, shared across every torrent. Zero or less means unlimited.</summary>
    long GlobalUploadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// URL to a plain-text tracker list (one announce URL per line, e.g.
    /// https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best_ip.txt),
    /// re-fetched on every launch. Combined with <see cref="DefaultTrackerListText"/> and
    /// upserted into every non-private torrent's own tracker list. Empty disables it.
    /// </summary>
    string DefaultTrackerListUrl { get; set; }

    /// <summary>
    /// User-supplied tracker list (one announce URL per line), combined with
    /// <see cref="DefaultTrackerListUrl"/>'s fetched content the same way. Empty disables it.
    /// </summary>
    string DefaultTrackerListText { get; set; }

    /// <summary>Minutes to keep seeding after a download completes, before the seed-until policy stops it automatically (unless <see cref="SeedUntilRatio"/> is hit first). Default 60.</summary>
    int SeedUntilMinutes { get; set; }

    /// <summary>Upload/download ratio to keep seeding until, before the seed-until policy stops it automatically (unless <see cref="SeedUntilMinutes"/> elapses first). Default 1.0.</summary>
    double SeedUntilRatio { get; set; }
}
