namespace bzTorrentClient.Engine.Settings;

public interface IClientSettings
{
    string DefaultDownloadDirectory { get; set; }
    int GlobalMaxConnections { get; set; }
    int MaxConnectionsPerTorrent { get; set; }
    int ListenPort { get; set; }
}
