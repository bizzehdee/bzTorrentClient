using System.Text.Json;
using bzTorrent.IO;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Settings;

public sealed class JsonClientSettingsStore : IClientSettingsStore
{
    private readonly string _filePath;

    public JsonClientSettingsStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be empty.", nameof(filePath));

        _filePath = filePath;
    }

    public IClientSettings Load()
    {
        if (!File.Exists(_filePath))
            return new ClientSettings();

        try
        {
            var json = File.ReadAllText(_filePath);
            var dto = JsonSerializer.Deserialize<ClientSettingsDto>(json);
            if (dto is null)
                return new ClientSettings();

            return new ClientSettings(dto.DefaultDownloadDirectory)
            {
                GlobalMaxConnections = dto.GlobalMaxConnections > 0 ? dto.GlobalMaxConnections : new ClientSettings().GlobalMaxConnections,
                MaxConnectionsPerTorrent = dto.MaxConnectionsPerTorrent > 0 ? dto.MaxConnectionsPerTorrent : new ClientSettings().MaxConnectionsPerTorrent,
                ListenPort = dto.ListenPort is > 0 and <= 65535 ? dto.ListenPort : new ClientSettings().ListenPort,
                // Nullable in the DTO (see EnableDht) so a settings file saved before these
                // existed reads as the default rather than false.
                RandomiseListenPortOnStartup = dto.RandomiseListenPortOnStartup ?? false,
                EnableUpnpPortForwarding = dto.EnableUpnpPortForwarding ?? false,
                // Unlike the settings above, zero is a valid, meaningful value here
                // ("unlimited") rather than "unset" — only clamp genuinely invalid negatives.
                GlobalDownloadLimitBytesPerSecond = Math.Max(0, dto.GlobalDownloadLimitBytesPerSecond),
                GlobalUploadLimitBytesPerSecond = Math.Max(0, dto.GlobalUploadLimitBytesPerSecond),
                DefaultTrackerListUrl = dto.DefaultTrackerListUrl ?? string.Empty,
                DefaultTrackerListText = dto.DefaultTrackerListText ?? string.Empty,
                SeedUntilMinutes = dto.SeedUntilMinutes > 0 ? dto.SeedUntilMinutes : new ClientSettings().SeedUntilMinutes,
                SeedUntilRatio = dto.SeedUntilRatio > 0 ? dto.SeedUntilRatio : new ClientSettings().SeedUntilRatio,
                RememberedDeleteFilesOnRemove = dto.RememberedDeleteFilesOnRemove,
                ColorTheme = Enum.TryParse<ColorTheme>(dto.ColorTheme, out var colorTheme) ? colorTheme : ColorTheme.Auto,
                // Nullable so a settings file saved before these existed round-trips as "on"
                // (the default) rather than silently disabling every discovery mechanism.
                EnableDht = dto.EnableDht ?? true,
                EnablePex = dto.EnablePex ?? true,
                EnableLpd = dto.EnableLpd ?? true,
                EncryptionMode = Enum.TryParse<PeerEncryptionMode>(dto.EncryptionMode, out var encryptionMode) ? encryptionMode : PeerEncryptionMode.PreferEncryption,
                DefaultAddTorrentState = Enum.TryParse<AddTorrentState>(dto.DefaultAddTorrentState, out var addTorrentState) ? addTorrentState : AddTorrentState.Paused,
                CloseToTray = dto.CloseToTray ?? true,
                LogDirectory = string.IsNullOrWhiteSpace(dto.LogDirectory) ? ClientSettings.GetPlatformDefaultLogDirectory() : dto.LogDirectory,
                LogMaxFileSizeBytes = dto.LogMaxFileSizeBytes > 0 ? dto.LogMaxFileSizeBytes : new ClientSettings().LogMaxFileSizeBytes,
                LogMaxAgeDays = dto.LogMaxAgeDays > 0 ? dto.LogMaxAgeDays : new ClientSettings().LogMaxAgeDays,
                IpBlocklistUrl = dto.IpBlocklistUrl ?? string.Empty,
                IpBlocklistFilePath = dto.IpBlocklistFilePath ?? string.Empty,
                IpBlocklistText = dto.IpBlocklistText ?? string.Empty,
            };
        }
        catch (JsonException)
        {
            return new ClientSettings();
        }
    }

    public void Save(IClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var dto = new ClientSettingsDto
        {
            DefaultDownloadDirectory = settings.DefaultDownloadDirectory,
            GlobalMaxConnections = settings.GlobalMaxConnections,
            MaxConnectionsPerTorrent = settings.MaxConnectionsPerTorrent,
            ListenPort = settings.ListenPort,
            RandomiseListenPortOnStartup = settings.RandomiseListenPortOnStartup,
            EnableUpnpPortForwarding = settings.EnableUpnpPortForwarding,
            GlobalDownloadLimitBytesPerSecond = settings.GlobalDownloadLimitBytesPerSecond,
            GlobalUploadLimitBytesPerSecond = settings.GlobalUploadLimitBytesPerSecond,
            DefaultTrackerListUrl = settings.DefaultTrackerListUrl,
            DefaultTrackerListText = settings.DefaultTrackerListText,
            SeedUntilMinutes = settings.SeedUntilMinutes,
            SeedUntilRatio = settings.SeedUntilRatio,
            RememberedDeleteFilesOnRemove = settings.RememberedDeleteFilesOnRemove,
            ColorTheme = settings.ColorTheme.ToString(),
            EnableDht = settings.EnableDht,
            EnablePex = settings.EnablePex,
            EnableLpd = settings.EnableLpd,
            EncryptionMode = settings.EncryptionMode.ToString(),
            DefaultAddTorrentState = settings.DefaultAddTorrentState.ToString(),
            CloseToTray = settings.CloseToTray,
            LogDirectory = settings.LogDirectory,
            LogMaxFileSizeBytes = settings.LogMaxFileSizeBytes,
            LogMaxAgeDays = settings.LogMaxAgeDays,
            IpBlocklistUrl = settings.IpBlocklistUrl,
            IpBlocklistFilePath = settings.IpBlocklistFilePath,
            IpBlocklistText = settings.IpBlocklistText,
        };

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ClientSettingsDto
    {
        public string DefaultDownloadDirectory { get; set; } = string.Empty;
        public int GlobalMaxConnections { get; set; }
        public int MaxConnectionsPerTorrent { get; set; }
        public int ListenPort { get; set; }
        public bool? RandomiseListenPortOnStartup { get; set; }
        public bool? EnableUpnpPortForwarding { get; set; }
        public long GlobalDownloadLimitBytesPerSecond { get; set; }
        public long GlobalUploadLimitBytesPerSecond { get; set; }
        public string DefaultTrackerListUrl { get; set; } = string.Empty;
        public string DefaultTrackerListText { get; set; } = string.Empty;
        public int SeedUntilMinutes { get; set; }
        public double SeedUntilRatio { get; set; }
        public bool? RememberedDeleteFilesOnRemove { get; set; }
        public string? ColorTheme { get; set; }
        public bool? EnableDht { get; set; }
        public bool? EnablePex { get; set; }
        public bool? EnableLpd { get; set; }
        public string? EncryptionMode { get; set; }
        public string? DefaultAddTorrentState { get; set; }
        public bool? CloseToTray { get; set; }
        public string? LogDirectory { get; set; }
        public long LogMaxFileSizeBytes { get; set; }
        public int LogMaxAgeDays { get; set; }
        public string? IpBlocklistUrl { get; set; }
        public string? IpBlocklistFilePath { get; set; }
        public string? IpBlocklistText { get; set; }
    }
}
