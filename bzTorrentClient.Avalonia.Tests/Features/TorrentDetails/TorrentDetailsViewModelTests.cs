using bzTorrent.Data;
using bzTorrentClient.Avalonia.Features.TorrentDetails;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.TorrentDetails;

public class TorrentDetailsViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-details-{Guid.NewGuid():N}");

    public TorrentDetailsViewModelTests()
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
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 0, 0) };
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.Contains(viewModel.Peers, p => p.EndPoint.Port == 6881);
    }

    [Fact]
    public async Task ShowSession_PeerRow_ReflectsUploadAndDownloadSpeed()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 0, 0) };
        var viewModel = new TorrentDetailsViewModel(sessionManager);
        viewModel.ShowSession(session.Id);

        await Task.Delay(600); // Exceed PeerRowViewModel's minimum sample interval.
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 1000, 2000) };
        viewModel.Refresh();

        var peer = Assert.Single(viewModel.Peers);
        Assert.True(peer.IsDownloading);
        Assert.True(peer.IsUploading);
        Assert.NotEqual("—", peer.DownloadSpeedText);
        Assert.NotEqual("—", peer.UploadSpeedText);
    }

    [Fact]
    public async Task ShowSession_MagnetWithoutMetadataYet_IsFetchingMetadataAndFilesEmpty()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.True(viewModel.IsFetchingMetadata);
        Assert.Empty(viewModel.Files);
    }

    [Fact]
    public async Task ShowSession_PartiallyDownloadedFile_ComputesPerFileProgress()
    {
        const long pieceSize = 16;
        var sourceFile = Path.Combine(_tempDir, "content.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, (int)pieceSize * 3).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile, pieceSize: pieceSize);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, false);
        session.MarkPieceVerified(0);

        var viewModel = new TorrentDetailsViewModel(sessionManager);
        viewModel.ShowSession(session.Id);

        Assert.False(viewModel.IsFetchingMetadata);
        var fileRow = Assert.Single(viewModel.Files);
        Assert.Equal("content.bin", fileRow.Filename);
        Assert.Equal(33.3, fileRow.ProgressPercent);
    }
}
