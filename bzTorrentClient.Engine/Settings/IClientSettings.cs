using bzTorrent.IO;
using bzTorrentClient.Engine.Sessions;

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

    /// <summary>
    /// Remembered answer to "also delete downloaded files?" when removing a torrent, set
    /// when the user checks "don't ask me again" on that dialog. Null means always ask.
    /// </summary>
    bool? RememberedDeleteFilesOnRemove { get; set; }

    /// <summary>Light/Dark/Auto (follow the OS). Default Auto.</summary>
    ColorTheme ColorTheme { get; set; }

    /// <summary>Whether to participate in the BitTorrent DHT (BEP-5) for peer discovery. Ignored for private torrents, which must use the tracker only. Default true.</summary>
    bool EnableDht { get; set; }

    /// <summary>Whether to exchange peers with connected peers via PEX (BEP-11). Ignored for private torrents. Default true.</summary>
    bool EnablePex { get; set; }

    /// <summary>Whether to announce/discover peers on the local network via Local Peer Discovery (BEP-14). Ignored for private torrents. Default true.</summary>
    bool EnableLpd { get; set; }

    /// <summary>
    /// How outbound peer connections negotiate MSE/PE encryption: PlainText disables it,
    /// PreferEncryption ("allow") negotiates it but falls back to plaintext, RequireEncryption
    /// refuses any connection that won't encrypt. Default PreferEncryption.
    /// </summary>
    PeerEncryptionMode EncryptionMode { get; set; }

    /// <summary>The state a newly added torrent lands in by default (Paused/Started/Stopped). Default Paused.</summary>
    AddTorrentState DefaultAddTorrentState { get; set; }

    /// <summary>Directory debug log files are written to.</summary>
    string LogDirectory { get; set; }

    /// <summary>A log file is rotated (a new one started) once it reaches this size. Default 100,000 bytes (100KB).</summary>
    long LogMaxFileSizeBytes { get; set; }

    /// <summary>Log files older than this many days are deleted. Default 7.</summary>
    int LogMaxAgeDays { get; set; }

    /// <summary>
    /// URL to a plain-text IP blocklist (single IPs, CIDR ranges, and/or
    /// eMule/PeerGuardian-style "description:start_ip-end_ip" ranges, one per line),
    /// re-fetched on every launch. Combined with <see cref="IpBlocklistFilePath"/> and
    /// <see cref="IpBlocklistText"/>. Empty disables it.
    /// </summary>
    string IpBlocklistUrl { get; set; }

    /// <summary>Local file path to an IP blocklist, same format as <see cref="IpBlocklistUrl"/>, re-read on every launch. Empty disables it.</summary>
    string IpBlocklistFilePath { get; set; }

    /// <summary>User-supplied IP blocklist entries (one per line), same format and combined the same way as <see cref="IpBlocklistUrl"/>. Empty disables it.</summary>
    string IpBlocklistText { get; set; }
}
