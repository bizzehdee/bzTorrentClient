using bzTorrent.IO;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Settings;

public sealed class ClientSettings : IClientSettings
{
    public string DefaultDownloadDirectory { get; set; }
    public int GlobalMaxConnections { get; set; } = 200;
    public int MaxConnectionsPerTorrent { get; set; } = 50;
    public int ListenPort { get; set; } = 6881;
    public bool RandomiseListenPortOnStartup { get; set; }
    public bool EnableUpnpPortForwarding { get; set; }
    public long GlobalDownloadLimitBytesPerSecond { get; set; }
    public long GlobalUploadLimitBytesPerSecond { get; set; }
    public string DefaultTrackerListUrl { get; set; } = string.Empty;
    public string DefaultTrackerListText { get; set; } = string.Empty;
    public int SeedUntilMinutes { get; set; } = 60;
    public double SeedUntilRatio { get; set; } = 1.0;
    public bool? RememberedDeleteFilesOnRemove { get; set; }
    public ColorTheme ColorTheme { get; set; } = ColorTheme.Auto;
    public bool EnableDht { get; set; } = true;
    public bool EnablePex { get; set; } = true;
    public bool EnableLpd { get; set; } = true;
    public PeerEncryptionMode EncryptionMode { get; set; } = PeerEncryptionMode.PreferEncryption;
    public AddTorrentState DefaultAddTorrentState { get; set; } = AddTorrentState.Paused;
    public string LogDirectory { get; set; }
    public long LogMaxFileSizeBytes { get; set; } = 100_000;
    public int LogMaxAgeDays { get; set; } = 7;
    public string IpBlocklistUrl { get; set; } = string.Empty;
    public string IpBlocklistFilePath { get; set; } = string.Empty;
    public string IpBlocklistText { get; set; } = string.Empty;

    public ClientSettings(string? defaultDownloadDirectory = null)
    {
        DefaultDownloadDirectory = string.IsNullOrWhiteSpace(defaultDownloadDirectory)
            ? GetPlatformDefaultDownloadDirectory()
            : defaultDownloadDirectory;
        LogDirectory = GetPlatformDefaultLogDirectory();
    }

    public static string GetPlatformDefaultDownloadDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string GetPlatformDefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bzTorrentClient", "logs");
}
