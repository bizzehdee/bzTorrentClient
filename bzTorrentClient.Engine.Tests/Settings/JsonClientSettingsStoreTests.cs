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
            GlobalDownloadLimitBytesPerSecond = 512_000,
            GlobalUploadLimitBytesPerSecond = 128_000,
            DefaultTrackerListUrl = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best_ip.txt",
            DefaultTrackerListText = "udp://tracker.example.com:1337/announce\nhttp://tracker2.example.com/announce",
            SeedUntilMinutes = 120,
            SeedUntilRatio = 2.5,
            ColorTheme = ColorTheme.Dark,
        };

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal("/custom/downloads", reloaded.DefaultDownloadDirectory);
        Assert.Equal(42, reloaded.GlobalMaxConnections);
        Assert.Equal(7, reloaded.MaxConnectionsPerTorrent);
        Assert.Equal(12345, reloaded.ListenPort);
        Assert.Equal(512_000, reloaded.GlobalDownloadLimitBytesPerSecond);
        Assert.Equal(128_000, reloaded.GlobalUploadLimitBytesPerSecond);
        Assert.Equal(settings.DefaultTrackerListUrl, reloaded.DefaultTrackerListUrl);
        Assert.Equal(settings.DefaultTrackerListText, reloaded.DefaultTrackerListText);
        Assert.Equal(120, reloaded.SeedUntilMinutes);
        Assert.Equal(2.5, reloaded.SeedUntilRatio);
        Assert.Equal(ColorTheme.Dark, reloaded.ColorTheme);
    }

    [Fact]
    public void Load_MissingFile_DefaultsColorThemeToAuto()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(ColorTheme.Auto, settings.ColorTheme);
    }

    [Fact]
    public void SaveThenLoad_ZeroSpeedLimits_StaysZeroNotDefault()
    {
        // Unlike the other numeric settings, 0 is a meaningful value here ("unlimited"),
        // not a sentinel for "unset" — it must round-trip as 0, not fall back to some
        // non-zero default the way GlobalMaxConnections etc. do.
        var store = new JsonClientSettingsStore(_filePath);
        var settings = new ClientSettings("/custom/downloads")
        {
            GlobalDownloadLimitBytesPerSecond = 0,
            GlobalUploadLimitBytesPerSecond = 0,
        };

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal(0, reloaded.GlobalDownloadLimitBytesPerSecond);
        Assert.Equal(0, reloaded.GlobalUploadLimitBytesPerSecond);
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
