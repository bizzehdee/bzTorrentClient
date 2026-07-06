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
    }
}
