using bzTorrent.Data;
using bzTorrentClient.Avalonia;
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
    public async Task ShowSession_TrackerWithRuntimeStatus_PopulatesTrackerRow()
    {
        var sessionManager = new FakeSessionManager();
        var magnetUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=http://tracker.example/announce";
        var session = await sessionManager.AddAsync(new TorrentAddSource.Magnet(magnetUri), "/downloads", false);
        var lastAnnounce = DateTime.UtcNow;
        sessionManager.TrackerStatuses[session.Id] = new List<TrackerStatus>
        {
            new("http://tracker.example/announce", PeersFound: 7, Seeders: 3, Leechers: 2, LastAnnounceUtc: lastAnnounce, LastError: null),
        };
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        var row = Assert.Single(viewModel.Trackers);
        Assert.Equal("http://tracker.example/announce", row.Url);
        Assert.Equal(7, row.PeersFound);
        Assert.Equal("3", row.SeedersText);
        Assert.Equal("2", row.LeechersText);
        Assert.NotEqual("Not yet announced", row.LastAnnounceText);
    }

    [Fact]
    public async Task ShowSession_TrackerWithNoRuntimeStatusYet_ShowsNotYetAnnounced()
    {
        var sessionManager = new FakeSessionManager();
        var magnetUri = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=http://tracker.example/announce";
        var session = await sessionManager.AddAsync(new TorrentAddSource.Magnet(magnetUri), "/downloads", false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        var row = Assert.Single(viewModel.Trackers);
        Assert.Equal("—", row.SeedersText);
        Assert.Equal("—", row.LeechersText);
        Assert.Equal("Not yet announced", row.LastAnnounceText);
    }

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
    public async Task ShowSession_ValidSession_PopulatesInfoTabFields()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.Equal(ByteFormat.Bytes(0), viewModel.TotalSizeText);
        Assert.Equal("No", viewModel.IsPrivateText);
        Assert.Equal("—", viewModel.Comment);
        Assert.Equal("—", viewModel.PieceInfoText);
        Assert.Equal("—", viewModel.CreatedByText);
        Assert.Equal("—", viewModel.CreatedOnText);
        Assert.NotEmpty(viewModel.DateAddedText);
    }

    [Fact]
    public async Task ShowSession_TorrentFileWithMetadata_PopulatesInfoTabFromMetadata()
    {
        var sourceFile = Path.Combine(_tempDir, "content.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 16 * 3).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile, pieceSize: 16, comment: "test comment", isPrivate: true);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, false);
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.Equal(ByteFormat.Bytes(48), viewModel.TotalSizeText);
        Assert.Equal("Yes", viewModel.IsPrivateText);
        Assert.Equal("test comment", viewModel.Comment);
        Assert.Equal($"3 pieces × {ByteFormat.Bytes(16)}", viewModel.PieceInfoText);
    }

    [Fact]
    public async Task ShowSession_PopulatesPeersFromRuntimeInfoProvider()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 0, 0, PeerTransportKind.Tcp, false) };
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        Assert.Contains(viewModel.Peers, p => p.EndPoint.Port == 6881);
    }

    [Fact]
    public async Task ShowSession_PeerRow_ReflectsTransportAndEncryption()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 0, 0, PeerTransportKind.Utp, true) };
        var viewModel = new TorrentDetailsViewModel(sessionManager);

        viewModel.ShowSession(session.Id);

        var peer = Assert.Single(viewModel.Peers);
        Assert.Equal(PeerTransportKind.Utp, peer.Transport);
        Assert.True(peer.IsEncrypted);
    }

    [Fact]
    public async Task ShowSession_PeerRow_ReflectsUploadAndDownloadSpeed()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 0, 0, PeerTransportKind.Tcp, false) };
        var viewModel = new TorrentDetailsViewModel(sessionManager);
        viewModel.ShowSession(session.Id);

        await Task.Delay(600); // Exceed PeerRowViewModel's minimum sample interval.
        sessionManager.ConnectedPeers[session.Id] = new List<PeerConnectionInfo> { new(endpoint, 1000, 2000, PeerTransportKind.Tcp, true) };
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
        Assert.Equal(ByteFormat.Bytes(fileRow.FileSize), fileRow.FileSizeText);
    }
}
