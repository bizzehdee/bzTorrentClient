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
}
