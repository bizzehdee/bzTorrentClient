using bzTorrentClient.Avalonia.Features.AddTorrent;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Avalonia.Tests.Features.AddTorrent;

public class AddTorrentViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-addtorrent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

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
    public async Task AddCommand_MagnetInput_AddsSessionAndRaisesCompleted()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Input = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        var completed = false;
        viewModel.Completed += (_, _) => completed = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.True(completed);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Single(sessionManager.Sessions);
    }

    [Fact]
    public async Task AddCommand_InfoHashInput_AddsTrackerlessSession()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Input = "0123456789abcdef0123456789abcdef01234567";

        await viewModel.AddCommand.ExecuteAsync(null);

        var session = Assert.Single(sessionManager.Sessions);
        Assert.IsType<TorrentAddSource.Magnet>(session.Source);
    }

    [Fact]
    public async Task AddCommand_ExistingFilePathInput_AddsSessionFromFile()
    {
        var sourceFile = Path.Combine(_tempDir, "content.bin");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3, 4 });
        var builtMetadata = bzTorrent.Data.Metadata.CreateFromPath(sourceFile, pieceSize: 16);
        var torrentFilePath = Path.Combine(_tempDir, "content.torrent");
        builtMetadata.SaveToFile(torrentFilePath);

        var (viewModel, sessionManager) = Create();
        viewModel.Input = torrentFilePath;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Single(sessionManager.Sessions);
    }

    [Fact]
    public async Task AddCommand_InvalidInput_SetsErrorAndDoesNotRaiseCompleted()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Input = "not-a-magnet-or-hash-or-path";

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
        viewModel.Input = "0123456789abcdef0123456789abcdef01234567";
        viewModel.StartPaused = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.Equal(TorrentState.Paused, sessionManager.Sessions.Single().State);
    }

    [Fact]
    public async Task AddCommand_StartPausedFalse_SessionStartsImmediately()
    {
        var (viewModel, sessionManager) = Create();
        viewModel.Input = "0123456789abcdef0123456789abcdef01234567";
        viewModel.StartPaused = false;

        await viewModel.AddCommand.ExecuteAsync(null);

        Assert.Equal(TorrentState.Active, sessionManager.Sessions.Single().State);
    }
}
