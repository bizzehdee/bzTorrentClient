using bzTorrent.IO;
using bzTorrentClient.Engine.Sessions;
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
            RandomiseListenPortOnStartup = true,
            EnableUpnpPortForwarding = true,
            GlobalDownloadLimitBytesPerSecond = 512_000,
            GlobalUploadLimitBytesPerSecond = 128_000,
            DefaultTrackerListUrl = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best_ip.txt",
            DefaultTrackerListText = "udp://tracker.example.com:1337/announce\nhttp://tracker2.example.com/announce",
            SeedUntilMinutes = 120,
            SeedUntilRatio = 2.5,
            ColorTheme = ColorTheme.Dark,
            EnableDht = false,
            EnablePex = false,
            EnableLpd = false,
            EncryptionMode = PeerEncryptionMode.RequireEncryption,
            DefaultAddTorrentState = AddTorrentState.Started,
            LogDirectory = "/custom/logs",
            LogMaxFileSizeBytes = 250_000,
            LogMaxAgeDays = 14,
            IpBlocklistUrl = "https://example.com/blocklist.txt",
            IpBlocklistFilePath = "/custom/blocklist.txt",
            IpBlocklistText = "1.2.3.4",
        };

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal("/custom/downloads", reloaded.DefaultDownloadDirectory);
        Assert.Equal(42, reloaded.GlobalMaxConnections);
        Assert.Equal(7, reloaded.MaxConnectionsPerTorrent);
        Assert.Equal(12345, reloaded.ListenPort);
        Assert.True(reloaded.RandomiseListenPortOnStartup);
        Assert.True(reloaded.EnableUpnpPortForwarding);
        Assert.Equal(512_000, reloaded.GlobalDownloadLimitBytesPerSecond);
        Assert.Equal(128_000, reloaded.GlobalUploadLimitBytesPerSecond);
        Assert.Equal(settings.DefaultTrackerListUrl, reloaded.DefaultTrackerListUrl);
        Assert.Equal(settings.DefaultTrackerListText, reloaded.DefaultTrackerListText);
        Assert.Equal(120, reloaded.SeedUntilMinutes);
        Assert.Equal(2.5, reloaded.SeedUntilRatio);
        Assert.Equal(ColorTheme.Dark, reloaded.ColorTheme);
        Assert.False(reloaded.EnableDht);
        Assert.False(reloaded.EnablePex);
        Assert.False(reloaded.EnableLpd);
        Assert.Equal(PeerEncryptionMode.RequireEncryption, reloaded.EncryptionMode);
        Assert.Equal(AddTorrentState.Started, reloaded.DefaultAddTorrentState);
        Assert.Equal("/custom/logs", reloaded.LogDirectory);
        Assert.Equal(250_000, reloaded.LogMaxFileSizeBytes);
        Assert.Equal(14, reloaded.LogMaxAgeDays);
        Assert.Equal("https://example.com/blocklist.txt", reloaded.IpBlocklistUrl);
        Assert.Equal("/custom/blocklist.txt", reloaded.IpBlocklistFilePath);
        Assert.Equal("1.2.3.4", reloaded.IpBlocklistText);
    }

    [Fact]
    public void Load_MissingFile_DefaultsEncryptionModeToPreferEncryption()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(PeerEncryptionMode.PreferEncryption, settings.EncryptionMode);
    }

    [Fact]
    public void Load_MissingFile_DefaultsAddTorrentStateToPaused()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(AddTorrentState.Paused, settings.DefaultAddTorrentState);
    }

    [Fact]
    public void Load_MissingFile_DefaultsLoggingSettings()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(ClientSettings.GetPlatformDefaultLogDirectory(), settings.LogDirectory);
        Assert.Equal(100_000, settings.LogMaxFileSizeBytes);
        Assert.Equal(7, settings.LogMaxAgeDays);
    }

    [Fact]
    public void Load_MissingFile_DefaultsIpBlocklistSettingsToEmpty()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(string.Empty, settings.IpBlocklistUrl);
        Assert.Equal(string.Empty, settings.IpBlocklistFilePath);
        Assert.Equal(string.Empty, settings.IpBlocklistText);
    }

    [Fact]
    public void Load_MissingFile_DefaultsColorThemeToAuto()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.Equal(ColorTheme.Auto, settings.ColorTheme);
    }

    [Fact]
    public void Load_MissingFile_DefaultsDiscoveryTogglesToTrue()
    {
        var store = new JsonClientSettingsStore(_filePath);
        var settings = store.Load();

        Assert.True(settings.EnableDht);
        Assert.True(settings.EnablePex);
        Assert.True(settings.EnableLpd);
    }

    [Fact]
    public void Load_SettingsFileFromBeforeDiscoveryTogglesExisted_DefaultsToTrue()
    {
        // A settings.json saved by an older build won't have these keys at all - they
        // must come back as enabled (the default), not silently disabled.
        File.WriteAllText(_filePath, "{ \"DefaultDownloadDirectory\": \"/downloads\" }");
        var store = new JsonClientSettingsStore(_filePath);

        var settings = store.Load();

        Assert.True(settings.EnableDht);
        Assert.True(settings.EnablePex);
        Assert.True(settings.EnableLpd);
    }

    [Fact]
    public void Load_SettingsFileFromBeforePortTogglesExisted_DefaultsToFalse()
    {
        // Port randomisation and UPnP are opt-in, so an older settings.json without the keys
        // must come back with both off rather than surprising the user by enabling them.
        File.WriteAllText(_filePath, "{ \"DefaultDownloadDirectory\": \"/downloads\" }");
        var store = new JsonClientSettingsStore(_filePath);

        var settings = store.Load();

        Assert.False(settings.RandomiseListenPortOnStartup);
        Assert.False(settings.EnableUpnpPortForwarding);
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
