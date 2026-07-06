using bzTorrentClient.Avalonia.Features.TorrentList;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.TorrentList;

public class TorrentListViewModelTests
{
    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task Refresh_AddsRowForNewSession()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);

        viewModel.Refresh();

        var row = Assert.Single(viewModel.Torrents);
        Assert.Equal(session.Id, row.Id);
        Assert.Equal(TorrentState.Paused, row.State);
    }

    [Fact]
    public async Task Refresh_RemovesRowForDeletedSession()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();

        await sessionManager.RemoveAsync(session.Id);
        viewModel.Refresh();

        Assert.Empty(viewModel.Torrents);
    }

    [Fact]
    public async Task Refresh_ReflectsPeerCountFromRuntimeInfoProvider()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        sessionManager.NetworkStats[session.Id] = new TorrentNetworkStats(5, 0, 0, 0, 0, 0, 0, 0);
        var viewModel = new TorrentListViewModel(sessionManager);

        viewModel.Refresh();

        Assert.Equal(5, viewModel.Torrents.Single().PeerCount);
    }

    [Fact]
    public async Task StartCommand_StartsTheGivenRow()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var row = viewModel.Torrents.Single();

        await row.StartCommand.ExecuteAsync(null);

        Assert.Contains(("Start", session.Id), sessionManager.Calls);
    }

    [Fact]
    public async Task PauseStopRemoveCommands_OperateOnGivenRow()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", true);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var row = viewModel.Torrents.Single();

        await row.PauseCommand.ExecuteAsync(null);
        await row.StopCommand.ExecuteAsync(null);
        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Contains(("Pause", session.Id), sessionManager.Calls);
        Assert.Contains(("Stop", session.Id), sessionManager.Calls);
        Assert.Contains(("Remove", session.Id), sessionManager.Calls);
    }

    [Fact]
    public async Task RowStartCommand_DoesNotBlockOnASlowStart()
    {
        // Regression test: Start used to be one command instance shared by every row via
        // CommandParameter, and AsyncRelayCommand disables itself globally while IsRunning —
        // so a slow StartAsync (e.g. a magnet metadata fetch) left every row's Start button
        // looking disabled, and Stop (a different command) could never re-enable it. Each row
        // now owns its own command, so a slow start on one row must not affect another row.
        var sessionManager = new FakeSessionManager();
        var slowSession = await sessionManager.AddAsync(Source("0123456789abcdef0123456789abcdef01234567"), "/downloads", false);
        var fastSession = await sessionManager.AddAsync(Source("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "/downloads", false);
        sessionManager.StartDelay = TimeSpan.FromMilliseconds(200);
        sessionManager.SlowStartSessionId = slowSession.Id;

        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var slowRow = viewModel.Torrents.Single(r => r.Id == slowSession.Id);
        var fastRow = viewModel.Torrents.Single(r => r.Id == fastSession.Id);

        var slowTask = slowRow.StartCommand.ExecuteAsync(null);

        Assert.True(slowRow.StartCommand.IsRunning);
        Assert.False(fastRow.StartCommand.IsRunning);

        await slowTask;
    }

    [Fact]
    public async Task SelectingATorrent_RaisesSelectionChanged()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();

        Guid? selected = null;
        viewModel.SelectionChanged += (_, id) => selected = id;

        viewModel.SelectedTorrent = viewModel.Torrents.Single();

        Assert.Equal(session.Id, selected);
    }
}
