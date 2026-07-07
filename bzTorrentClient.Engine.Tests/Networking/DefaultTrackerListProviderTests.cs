using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Settings;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

public class DefaultTrackerListProviderTests : IDisposable
{
    private readonly string _cacheFilePath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-trackercache-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_cacheFilePath))
            File.Delete(_cacheFilePath);
    }

    private static ClientSettings Settings(string url = "", string text = "") => new("/downloads")
    {
        DefaultTrackerListUrl = url,
        DefaultTrackerListText = text,
    };

    [Fact]
    public void GetTrackers_NoUrlOrText_ReturnsEmpty()
    {
        var provider = new DefaultTrackerListProvider(Settings(), _cacheFilePath);

        Assert.Empty(provider.GetTrackers());
    }

    [Fact]
    public async Task RefreshAsync_FetchesAndCachesUrlContent()
    {
        var settings = Settings(url: "https://example.com/trackers.txt");
        var provider = new DefaultTrackerListProvider(
            settings,
            _cacheFilePath,
            fetcher: (_, _) => Task.FromResult("udp://tracker.example.com:1337/announce"));

        await provider.RefreshAsync();

        Assert.Contains("udp://tracker.example.com:1337/announce", provider.GetTrackers());
        Assert.Equal("udp://tracker.example.com:1337/announce", File.ReadAllText(_cacheFilePath));
    }

    [Fact]
    public async Task RefreshAsync_FetchThrows_KeepsPreviouslyCachedList()
    {
        var settings = Settings(url: "https://example.com/trackers.txt");
        File.WriteAllText(_cacheFilePath, "udp://old-tracker.example.com:80/announce");

        var provider = new DefaultTrackerListProvider(
            settings,
            _cacheFilePath,
            fetcher: (_, _) => throw new HttpRequestException("network down"));

        await provider.RefreshAsync();

        Assert.Contains("udp://old-tracker.example.com:80/announce", provider.GetTrackers());
    }

    [Fact]
    public void GetTrackers_InvalidLines_AreSilentlyIgnored()
    {
        var settings = Settings(text: string.Join('\n',
            "udp://good.example.com:1337/announce",
            "",
            "not a url at all",
            "ftp://wrong-scheme.example.com/announce",
            "http://good2.example.com/announce"));

        var provider = new DefaultTrackerListProvider(settings, _cacheFilePath);

        var trackers = provider.GetTrackers();

        Assert.Equal(new[] { "udp://good.example.com:1337/announce", "http://good2.example.com/announce" }, trackers);
    }

    [Fact]
    public async Task GetTrackers_CombinesUrlAndTextSources_Deduped()
    {
        var settings = Settings(
            url: "https://example.com/trackers.txt",
            text: "udp://shared.example.com:1337/announce\nhttp://text-only.example.com/announce");

        var provider = new DefaultTrackerListProvider(
            settings,
            _cacheFilePath,
            fetcher: (_, _) => Task.FromResult("udp://shared.example.com:1337/announce\nudp://url-only.example.com:80/announce"));

        await provider.RefreshAsync();

        var trackers = provider.GetTrackers();

        Assert.Equal(
            new[]
            {
                "udp://shared.example.com:1337/announce",
                "udp://url-only.example.com:80/announce",
                "http://text-only.example.com/announce",
            },
            trackers);
    }

    [Fact]
    public void Constructor_ExistingCacheFile_LoadsItWithoutRefresh()
    {
        File.WriteAllText(_cacheFilePath, "udp://cached.example.com:1337/announce");

        var provider = new DefaultTrackerListProvider(Settings(url: "https://example.com/trackers.txt"), _cacheFilePath);

        Assert.Contains("udp://cached.example.com:1337/announce", provider.GetTrackers());
    }
}
