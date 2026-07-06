using bzTorrentClient.Avalonia.Features.TorrentDetails;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.TorrentDetails;

public class TorrentDetailsViewModelTests
{
    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public void ShowSession_NoSelection_HasSelectionIsFalse()
    {
        var viewModel = new TorrentDetailsViewModel(new FakeSessionManager());

        viewModel.ShowSession(null);

        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public async Task ShowSession_ValidSession_PopulatesFields()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads/foo", false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.True(viewModel.HasSelection);
        Assert.Equal(session.Metadata.HashString, viewModel.InfoHash);
        Assert.Equal("/downloads/foo", viewModel.DownloadDirectory);
    }

    [Fact]
    public async Task ShowSession_ThenSessionRemoved_RefreshClearsSelection()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);
        viewModel.ShowSession(session.Id);

        await sessionManager.RemoveAsync(session.Id);
        viewModel.Refresh();

        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public async Task ShowSession_PopulatesPeersFromRuntimeInfoProvider()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        sessionManager.ConnectedPeers[session.Id] = new List<System.Net.IPEndPoint> { endpoint };
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.Contains(viewModel.Peers, p => p.Contains("6881"));
    }
}
