namespace bzTorrentClient.Engine.Settings;

public sealed class ClientSettings : IClientSettings
{
    public string DefaultDownloadDirectory { get; set; }
    public int GlobalMaxConnections { get; set; } = 200;
    public int MaxConnectionsPerTorrent { get; set; } = 50;
    public int ListenPort { get; set; } = 6881;
    public long GlobalDownloadLimitBytesPerSecond { get; set; }
    public long GlobalUploadLimitBytesPerSecond { get; set; }
    public string DefaultTrackerListUrl { get; set; } = string.Empty;
    public string DefaultTrackerListText { get; set; } = string.Empty;
    public int SeedUntilMinutes { get; set; } = 60;
    public double SeedUntilRatio { get; set; } = 1.0;
    public bool? RememberedDeleteFilesOnRemove { get; set; }
    public ColorTheme ColorTheme { get; set; } = ColorTheme.Auto;

    public ClientSettings(string? defaultDownloadDirectory = null)
    {
        DefaultDownloadDirectory = string.IsNullOrWhiteSpace(defaultDownloadDirectory)
            ? GetPlatformDefaultDownloadDirectory()
            : defaultDownloadDirectory;
    }

    public static string GetPlatformDefaultDownloadDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
