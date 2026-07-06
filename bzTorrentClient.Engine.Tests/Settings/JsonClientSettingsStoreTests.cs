using bzTorrentClient.Engine.Settings;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Settings;

public class JsonClientSettingsStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(ClientSettings.GetPlatformDefaultDownloadDirectory(), settings.DefaultDownloadDirectory);
        Assert.Equal(6881, settings.ListenPort);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = new ClientSettings("/custom/downloads")
        {
            GlobalMaxConnections = 42,
            MaxConnectionsPerTorrent = 7,
            ListenPort = 12345,
        };

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal("/custom/downloads", reloaded.DefaultDownloadDirectory);
        Assert.Equal(42, reloaded.GlobalMaxConnections);
        Assert.Equal(7, reloaded.MaxConnectionsPerTorrent);
        Assert.Equal(12345, reloaded.ListenPort);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "{ not valid json ");

        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(ClientSettings.GetPlatformDefaultDownloadDirectory(), settings.DefaultDownloadDirectory);
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-settings-dir-{Guid.NewGuid():N}", "settings.json");
        var store = new JsonClientSettingsStore(nestedPath);

        store.Save(new ClientSettings("/downloads"));

        Assert.True(File.Exists(nestedPath));
        Directory.Delete(Path.GetDirectoryName(nestedPath)!, recursive: true);
    }
}
