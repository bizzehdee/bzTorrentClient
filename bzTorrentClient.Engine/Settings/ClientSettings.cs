namespace bzTorrentClient.Engine.Settings;

public sealed class ClientSettings : IClientSettings
{
    public string DefaultDownloadDirectory { get; set; }
    public int GlobalMaxConnections { get; set; } = 200;
    public int MaxConnectionsPerTorrent { get; set; } = 50;
    public int ListenPort { get; set; } = 6881;

    public ClientSettings(string? defaultDownloadDirectory = null)
    {
        DefaultDownloadDirectory = string.IsNullOrWhiteSpace(defaultDownloadDirectory)
            ? GetPlatformDefaultDownloadDirectory()
            : defaultDownloadDirectory;
    }

    public static string GetPlatformDefaultDownloadDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
