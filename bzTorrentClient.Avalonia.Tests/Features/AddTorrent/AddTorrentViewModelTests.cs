using bzTorrentClient.Avalonia.Features.AddTorrent;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Avalonia.Tests.Features.AddTorrent;

public class AddTorrentViewModelTests
{
    private static (AddTorrentViewModel viewModel, FakeSessionManager sessionManager) Create()
    {
        var sessionManager = new FakeSessionManager();
        var pipeline = new TorrentAddPipeline(sessionManager);
        var settings = new ClientSettings("/downloads");
        return (new AddTorrentViewModel(pipeline, settings), sessionManager);
    }

    [Fact]
    public void Constructor_DefaultsDownloadDirectoryFromSettings()
    {
        var (viewModel, _) = Create();
        Assert.Equal("/downloads", viewModel.DownloadDirectory);
    }

    [Fact]
    public void SetModeCommand_ChangesMode()
    {
        var (viewModel, _) = Create();

        viewModel.SetModeCommand.Execute(AddTorrentMode.Magnet);

        Assert.Equal(AddTorrentMode.Magnet, viewModel.Mode);
    }

    [Fact]
    public async Task AddCommand_MagnetMode_AddsSessionAndRaisesCompleted()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Mode = AddTorrentMode.Magnet;
        viewModel.MagnetUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        var completed = false;
        viewModel.Completed += (_, _) => completed = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.True(completed);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Single(sessionManager.Sessions);
    }

    [Fact]
    public async Task AddCommand_InfoHashMode_AddsTrackerlessSession()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Mode = AddTorrentMode.InfoHash;
        viewModel.InfoHash = "0123456789abcdef0123456789abcdef01234567";

        await viewModel.AddCommand.ExecuteAsync(null);

        var session = Assert.Single(sessionManager.Sessions);
        Assert.IsType<TorrentAddSource.Magnet>(session.Source);
    }

    [Fact]
    public async Task AddCommand_InvalidMagnet_SetsErrorAndDoesNotRaiseCompleted()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Mode = AddTorrentMode.Magnet;
        viewModel.MagnetUri = "not-a-magnet";

        var completed = false;
        viewModel.Completed += (_, _) => completed = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.False(completed);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Empty(sessionManager.Sessions);
    }

    [Fact]
    public async Task AddCommand_StartPausedTrue_SessionLandsPaused()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Mode = AddTorrentMode.InfoHash;
        viewModel.InfoHash = "0123456789abcdef0123456789abcdef01234567";
        viewModel.StartPaused = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.Equal(TorrentState.Paused, sessionManager.Sessions.Single().State);
    }

    [Fact]
    public async Task AddCommand_StartPausedFalse_SessionStartsImmediately()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Mode = AddTorrentMode.InfoHash;
        viewModel.InfoHash = "0123456789abcdef0123456789abcdef01234567";
        viewModel.StartPaused = false;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.Equal(TorrentState.Active, sessionManager.Sessions.Single().State);
    }
}
