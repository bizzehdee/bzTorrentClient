using bzTorrent.Data;
using bzTorrentClient.Avalonia.Features.TorrentList;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.TorrentList;

public class TorrentListViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-torrentlist-{Guid.NewGuid():N}");

    public TorrentListViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    private TorrentAddSource.TorrentFile RealTorrentSource(string filename, int sizeBytes)
    {
        var sourceFile = Path.Combine(_tempDir, filename);
        File.WriteAllBytes(sourceFile, new byte[sizeBytes]);
        var metadata = Metadata.CreateFromPath(sourceFile);
        using var stream = new MemoryStream();
        metadata.Save(stream);
        return new TorrentAddSource.TorrentFile(stream.ToArray());
    }

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
    public async Task PauseStopCommands_OperateOnGivenRow()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", true);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var row = viewModel.Torrents.Single();

        await row.PauseCommand.ExecuteAsync(null);
        await row.StopCommand.ExecuteAsync(null);

        Assert.Contains(("Pause", session.Id), sessionManager.Calls);
        Assert.Contains(("Stop", session.Id), sessionManager.Calls);
    }

    [Fact]
    public async Task RemoveCommand_DoesNotRemoveDirectly_RaisesRemoveRequestedInstead()
    {
        // Removing a torrent may also delete files on disk, which needs user confirmation
        // first - so the row must not call ISessionManager.RemoveAsync itself.
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", true);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var row = viewModel.Torrents.Single();

        Guid? requestedId = null;
        viewModel.RemoveRequested += (_, id) => requestedId = id;

        row.RemoveCommand.Execute(null);

        Assert.Equal(session.Id, requestedId);
        Assert.DoesNotContain(("Remove", session.Id), sessionManager.Calls);
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

    [Fact]
    public async Task SortMode_Default_OrdersByAddedOrder()
    {
        var sessionManager = new FakeSessionManager();
        var first = await sessionManager.AddAsync(RealTorrentSource("b-first-added.bin", 10), "/downloads", false);
        var second = await sessionManager.AddAsync(RealTorrentSource("a-second-added.bin", 10), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);

        viewModel.Refresh();

        Assert.Equal(new[] { first.Id, second.Id }, viewModel.Torrents.Select(t => t.Id));
    }

    [Fact]
    public async Task SortMode_Size_OrdersLargestFirst()
    {
        var sessionManager = new FakeSessionManager();
        var small = await sessionManager.AddAsync(RealTorrentSource("small.bin", 10), "/downloads", false);
        var large = await sessionManager.AddAsync(RealTorrentSource("large.bin", 1000), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();

        viewModel.SortMode = TorrentSortMode.Size;

        Assert.Equal(new[] { large.Id, small.Id }, viewModel.Torrents.Select(t => t.Id));
    }

    [Fact]
    public async Task SortMode_Name_OrdersAlphabetically()
    {
        var sessionManager = new FakeSessionManager();
        var zebra = await sessionManager.AddAsync(RealTorrentSource("zebra.bin", 10), "/downloads", false);
        var apple = await sessionManager.AddAsync(RealTorrentSource("apple.bin", 10), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();

        viewModel.SortMode = TorrentSortMode.Name;

        Assert.Equal(new[] { apple.Id, zebra.Id }, viewModel.Torrents.Select(t => t.Id));
    }

    [Fact]
    public async Task SortMode_Progress_OrdersMostCompleteFirst()
    {
        var sessionManager = new FakeSessionManager();
        var behind = await sessionManager.AddAsync(RealTorrentSource("behind.bin", 100), "/downloads", false);
        var ahead = await sessionManager.AddAsync(RealTorrentSource("ahead.bin", 100), "/downloads", false);
        ahead.MarkPieceVerified(0);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();

        viewModel.SortMode = TorrentSortMode.Progress;

        Assert.Equal(new[] { ahead.Id, behind.Id }, viewModel.Torrents.Select(t => t.Id));
    }

    [Fact]
    public async Task SortMode_ChangingBack_PreservesSelection()
    {
        var sessionManager = new FakeSessionManager();
        var a = await sessionManager.AddAsync(RealTorrentSource("zzz.bin", 10), "/downloads", false);
        var b = await sessionManager.AddAsync(RealTorrentSource("aaa.bin", 10), "/downloads", false);
        var viewModel = new TorrentListViewModel(sessionManager);
        viewModel.Refresh();
        var selectedRow = viewModel.Torrents.Single(t => t.Id == a.Id);
        viewModel.SelectedTorrent = selectedRow;

        viewModel.SortMode = TorrentSortMode.Name;

        Assert.Same(selectedRow, viewModel.SelectedTorrent);
    }
}
