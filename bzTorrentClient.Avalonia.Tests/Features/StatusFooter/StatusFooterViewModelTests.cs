using bzTorrentClient.Avalonia.Features.StatusFooter;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.StatusFooter;

public class StatusFooterViewModelTests
{
    private static TorrentAddSource.Magnet Source(string hashHex) =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task Refresh_SumsDiscoveryCountsAcrossAllSessions()
    {
        var sessionManager = new FakeSessionManager();
        var sessionA = await sessionManager.AddAsync(Source("0123456789abcdef0123456789abcdef01234567"), "/downloads", false);
        var sessionB = await sessionManager.AddAsync(Source("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "/downloads", false);

        sessionManager.NetworkStats[sessionA.Id] = new TorrentNetworkStats(1, 0, 0, TrackerPeersFound: 2, DhtPeersFound: 3, LanPeersFound: 1, PexPeersFound: 5, DhtNodeCount: 10);
        sessionManager.NetworkStats[sessionB.Id] = new TorrentNetworkStats(1, 0, 0, TrackerPeersFound: 4, DhtPeersFound: 1, LanPeersFound: 0, PexPeersFound: 2, DhtNodeCount: 8);

        var viewModel = new StatusFooterViewModel(sessionManager);

        viewModel.Refresh();

        Assert.Equal(6, viewModel.TrackerPeersFound);
        Assert.Equal(4, viewModel.DhtPeersFound);
        Assert.Equal(1, viewModel.LanPeersFound);
        Assert.Equal(7, viewModel.PexPeersFound);
        Assert.Equal(18, viewModel.DhtNodeCount);
    }

    [Fact]
    public void Refresh_NoSessions_AllZero()
    {
        var sessionManager = new FakeSessionManager();
        var viewModel = new StatusFooterViewModel(sessionManager);

        viewModel.Refresh();

        Assert.Equal(0, viewModel.TrackerPeersFound);
        Assert.Equal(0, viewModel.DhtPeersFound);
        Assert.Equal(0, viewModel.PexPeersFound);
        Assert.Equal(0, viewModel.LanPeersFound);
        Assert.Equal(0, viewModel.DhtNodeCount);
    }
}
