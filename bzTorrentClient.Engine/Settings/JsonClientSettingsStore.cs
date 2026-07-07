using System.Text.Json;

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
                // Unlike the settings above, zero is a valid, meaningful value here
                // ("unlimited") rather than "unset" — only clamp genuinely invalid negatives.
                GlobalDownloadLimitBytesPerSecond = Math.Max(0, dto.GlobalDownloadLimitBytesPerSecond),
                GlobalUploadLimitBytesPerSecond = Math.Max(0, dto.GlobalUploadLimitBytesPerSecond),
                DefaultTrackerListUrl = dto.DefaultTrackerListUrl ?? string.Empty,
                DefaultTrackerListText = dto.DefaultTrackerListText ?? string.Empty,
                SeedUntilMinutes = dto.SeedUntilMinutes > 0 ? dto.SeedUntilMinutes : new ClientSettings().SeedUntilMinutes,
                SeedUntilRatio = dto.SeedUntilRatio > 0 ? dto.SeedUntilRatio : new ClientSettings().SeedUntilRatio,
                RememberedDeleteFilesOnRemove = dto.RememberedDeleteFilesOnRemove,
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
            GlobalDownloadLimitBytesPerSecond = settings.GlobalDownloadLimitBytesPerSecond,
            GlobalUploadLimitBytesPerSecond = settings.GlobalUploadLimitBytesPerSecond,
            DefaultTrackerListUrl = settings.DefaultTrackerListUrl,
            DefaultTrackerListText = settings.DefaultTrackerListText,
            SeedUntilMinutes = settings.SeedUntilMinutes,
            SeedUntilRatio = settings.SeedUntilRatio,
            RememberedDeleteFilesOnRemove = settings.RememberedDeleteFilesOnRemove,
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
        public long GlobalDownloadLimitBytesPerSecond { get; set; }
        public long GlobalUploadLimitBytesPerSecond { get; set; }
        public string DefaultTrackerListUrl { get; set; } = string.Empty;
        public string DefaultTrackerListText { get; set; } = string.Empty;
        public int SeedUntilMinutes { get; set; }
        public double SeedUntilRatio { get; set; }
        public bool? RememberedDeleteFilesOnRemove { get; set; }
    }
}
