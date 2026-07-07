using System.Net;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Settings;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

public class IpBlocklistProviderTests : IDisposable
{
    private readonly string _cacheFilePath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-blocklistcache-{Guid.NewGuid():N}.txt");
    private readonly string _localFilePath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-blocklistfile-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_cacheFilePath))
            File.Delete(_cacheFilePath);
        if (File.Exists(_localFilePath))
            File.Delete(_localFilePath);
    }

    private static ClientSettings Settings(string url = "", string filePath = "", string text = "") => new("/downloads")
    {
        IpBlocklistUrl = url,
        IpBlocklistFilePath = filePath,
        IpBlocklistText = text,
    };

    [Fact]
    public void IsBlocked_NoSources_NeverBlocksAnything()
    {
        var provider = new IpBlocklistProvider(Settings(), _cacheFilePath);

        Assert.False(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void IsBlocked_SingleIpEntry_BlocksOnlyThatAddress()
    {
        var provider = new IpBlocklistProvider(Settings(text: "1.2.3.4"), _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("1.2.3.5")));
    }

    [Fact]
    public void IsBlocked_CidrEntry_BlocksEntireRange()
    {
        var provider = new IpBlocklistProvider(Settings(text: "10.0.0.0/24"), _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("10.0.0.0")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("10.0.0.128")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("10.0.0.255")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("10.0.1.0")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("9.255.255.255")));
    }

    [Fact]
    public void IsBlocked_PeerGuardianStyleRange_BlocksInclusiveRange()
    {
        var provider = new IpBlocklistProvider(Settings(text: "Some Bad Range:5.6.7.8-5.6.7.20"), _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("5.6.7.8")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("5.6.7.15")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("5.6.7.20")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("5.6.7.7")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("5.6.7.21")));
    }

    [Fact]
    public void IsBlocked_CommentAndBlankLines_AreIgnored()
    {
        var provider = new IpBlocklistProvider(
            Settings(text: string.Join('\n', "# a comment", "", "; another comment", "1.2.3.4")),
            _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void IsBlocked_LocalFileSource_IsCombined()
    {
        File.WriteAllText(_localFilePath, "192.168.1.1");
        var provider = new IpBlocklistProvider(Settings(filePath: _localFilePath, text: "1.2.3.4"), _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("192.168.1.1")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public async Task RefreshAsync_FetchesAndCombinesUrlSource()
    {
        var settings = Settings(url: "https://example.com/blocklist.txt", text: "1.2.3.4");
        var provider = new IpBlocklistProvider(
            settings,
            _cacheFilePath,
            fetcher: (_, _) => Task.FromResult("5.6.7.8"));

        await provider.RefreshAsync();

        Assert.True(provider.IsBlocked(IPAddress.Parse("5.6.7.8")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
        Assert.Equal("5.6.7.8", File.ReadAllText(_cacheFilePath));
    }

    [Fact]
    public async Task RefreshAsync_FetchThrows_KeepsPreviouslyCachedList()
    {
        var settings = Settings(url: "https://example.com/blocklist.txt");
        File.WriteAllText(_cacheFilePath, "1.2.3.4");

        var provider = new IpBlocklistProvider(
            settings,
            _cacheFilePath,
            fetcher: (_, _) => throw new HttpRequestException("network down"));

        await provider.RefreshAsync();

        Assert.True(provider.IsBlocked(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void IsBlocked_NonIPv4Address_IsNeverBlocked()
    {
        var provider = new IpBlocklistProvider(Settings(text: "1.2.3.4"), _cacheFilePath);

        Assert.False(provider.IsBlocked(IPAddress.Parse("::1")));
    }

    [Fact]
    public void IsBlocked_MultipleDisjointRanges_EachMatchedIndependently()
    {
        var provider = new IpBlocklistProvider(
            Settings(text: string.Join('\n', "1.0.0.0/8", "50.0.0.0-50.0.0.10", "200.200.200.200")),
            _cacheFilePath);

        Assert.True(provider.IsBlocked(IPAddress.Parse("1.255.255.255")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("50.0.0.5")));
        Assert.True(provider.IsBlocked(IPAddress.Parse("200.200.200.200")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("2.0.0.0")));
        Assert.False(provider.IsBlocked(IPAddress.Parse("50.0.0.11")));
    }
}
