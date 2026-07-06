using bzTorrent.Data;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Networking;
using bzTorrentClient.Engine.Tests.Persistence;
using bzTorrentClient.Engine.Transfer;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class NetworkedSessionManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-networked-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private (NetworkedSessionManager manager, Dictionary<Guid, FakePeerSource> peerSources, Dictionary<Guid, FakePeerConnectionManager> connectionManagers, Dictionary<Guid, IPieceManager> pieceManagers)
        CreateManager()
    {
        var store = new InMemorySessionStore();
        var settings = new ClientSettings(_tempDir);
        var inner = new SessionManager(store, settings);

        var peerSources = new Dictionary<Guid, FakePeerSource>();
        var connectionManagers = new Dictionary<Guid, FakePeerConnectionManager>();
        var pieceManagers = new Dictionary<Guid, IPieceManager>();

        var manager = new NetworkedSessionManager(
            inner,
            settings,
            "-bz0001-000000000000",
            peerSourceFactory: (session, _) =>
            {
                var fake = new FakePeerSource();
                peerSources[session.Id] = fake;
                return fake;
            },
            connectionManagerFactory: (session, _, pieceManager) =>
            {
                var fake = new FakePeerConnectionManager();
                connectionManagers[session.Id] = fake;
                pieceManagers[session.Id] = pieceManager;
                return fake;
            },
            metadataFetchTimeout: TimeSpan.FromMilliseconds(100));

        return (manager, peerSources, connectionManagers, pieceManagers);
    }

    private static TorrentAddSource.TorrentFile RealTorrentFileSource() =>
        new(File.ReadAllBytes(Path.Combine("TestFiles", "UbuntuTestTorrent.torrent")));

    private static TorrentAddSource.Magnet MagnetOnlySource(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task StartAsync_TorrentFileSession_StartsPeerSourceAndConnectionManager()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, false);

        await manager.StartAsync(session.Id);

        Assert.True(peerSources[session.Id].Started);
        Assert.True(connectionManagers[session.Id].Started);
        Assert.Equal(TorrentState.Active, session.State);
    }

    [Fact]
    public async Task StartAsync_MagnetOnlySession_AttemptsMetadataFetchButLeavesStubIfNoPeers()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(MagnetOnlySource(), null, false);

        await manager.StartAsync(session.Id);

        // StartAsync no longer awaits the metadata fetch inline (it'd block the caller
        // for up to metadataFetchTimeout) — it runs detached, so give it time to finish.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // No peers were raised on the fake peer source, so the fetch times out quickly
        // and the session is left with its 0-piece stub metadata.
        Assert.Empty(session.Metadata.PieceHashes);
        Assert.True(connectionManagers[session.Id].Started);
    }

    [Fact]
    public async Task PeerFound_FromPeerSource_IsForwardedToConnectionManager()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, false);
        await manager.StartAsync(session.Id);

        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        peerSources[session.Id].Raise(endpoint);

        Assert.Contains(endpoint, connectionManagers[session.Id].Candidates);
    }

    [Fact]
    public async Task PauseAsync_PausesConnectionManagerButKeepsPeerSourceRunning()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, true);

        await manager.PauseAsync(session.Id);

        Assert.True(connectionManagers[session.Id].Paused);
        Assert.False(peerSources[session.Id].Stopped);
        Assert.False(peerSources[session.Id].Disposed);
    }

    [Fact]
    public async Task StopAsync_DisposesBothPeerSourceAndConnectionManager()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, true);

        await manager.StopAsync(session.Id);

        Assert.True(connectionManagers[session.Id].Disposed);
        Assert.True(peerSources[session.Id].Disposed);
    }

    [Fact]
    public async Task StartAsync_AfterStop_CreatesFreshRuntime()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, true);
        var firstConnectionManager = connectionManagers[session.Id];

        await manager.StopAsync(session.Id);
        await manager.StartAsync(session.Id);

        Assert.NotSame(firstConnectionManager, connectionManagers[session.Id]);
        Assert.True(connectionManagers[session.Id].Started);
    }

    [Fact]
    public async Task RemoveAsync_TearsDownRuntimeAndForgetsSession()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, true);

        await manager.RemoveAsync(session.Id);

        Assert.True(connectionManagers[session.Id].Disposed);
        Assert.True(peerSources[session.Id].Disposed);
        Assert.DoesNotContain(session, manager.Sessions);
    }

    [Fact]
    public async Task GetNetworkStats_CombinesPeerSourceAndConnectionManagerCounters()
    {
        var (manager, peerSources, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(RealTorrentFileSource(), null, true);

        peerSources[session.Id].TrackerPeersFound = 3;
        peerSources[session.Id].DhtPeersFound = 5;
        peerSources[session.Id].LanPeersFound = 1;
        peerSources[session.Id].DhtNodeCount = 20;
        connectionManagers[session.Id].BytesDownloaded = 1024;
        connectionManagers[session.Id].BytesUploaded = 256;
        connectionManagers[session.Id].PexPeersFound = 4;
        connectionManagers[session.Id].AddPeerCandidate(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881));

        var stats = ((ITorrentRuntimeInfoProvider)manager).GetNetworkStats(session.Id);

        Assert.Equal(1, stats.ActiveConnections);
        Assert.Equal(1024, stats.BytesDownloaded);
        Assert.Equal(256, stats.BytesUploaded);
        Assert.Equal(3, stats.TrackerPeersFound);
        Assert.Equal(5, stats.DhtPeersFound);
        Assert.Equal(1, stats.LanPeersFound);
        Assert.Equal(4, stats.PexPeersFound);
        Assert.Equal(20, stats.DhtNodeCount);
    }

    [Fact]
    public void GetNetworkStats_UnknownSession_ReturnsEmpty()
    {
        var (manager, _, _, _) = CreateManager();

        var stats = ((ITorrentRuntimeInfoProvider)manager).GetNetworkStats(Guid.NewGuid());

        Assert.Equal(TorrentNetworkStats.Empty, stats);
    }

    [Fact]
    public async Task StartAsync_MagnetOnlySession_ReturnsWellBeforeMetadataFetchTimeout()
    {
        // Regression test: StartAsync used to await the whole metadata fetch (up to
        // metadataFetchTimeout) before returning, which kept the UI's Start command
        // "running" — and its button looking disabled — for that entire window, even
        // after the user had already clicked Stop on the same torrent. StartAsync must
        // return promptly; the fetch continues in the background.
        var (manager, _, _, _) = CreateManager();
        var session = await manager.AddAsync(MagnetOnlySource(), null, false);

        var task = manager.StartAsync(session.Id);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(50)));

        Assert.Same(task, completed);
    }

    [Fact]
    public async Task StopAsync_WhileMetadataFetchStillPending_DoesNotResurrectTheRuntime()
    {
        var (manager, _, connectionManagers, _) = CreateManager();
        var session = await manager.AddAsync(MagnetOnlySource(), null, false);

        await manager.StartAsync(session.Id);
        await manager.StopAsync(session.Id);

        // Give the (now-orphaned) background metadata fetch time to time out and try to
        // start a connection manager — it must find its runtime gone and do nothing.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(TorrentState.Stopped, session.State);
    }

    [Fact]
    public async Task OnBlockReceived_CompletesPiece_UpdatesSessionProgress()
    {
        // Regression test: RarestFirstPieceManager clones session.PieceCompletion at
        // construction and tracked completion purely internally from then on — a piece
        // finishing and verifying never reached TorrentSession, so ProgressFraction (and
        // therefore the UI's "downloaded" bytes) stayed stuck at 0% even while data was
        // genuinely arriving and being written to disk. NetworkedSessionManager must wire
        // the piece manager's PieceCompleted event to session.MarkPieceVerified.
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "content.bin");
        var content = Enumerable.Range(0, 100).Select(b => (byte)b).ToArray();
        await File.WriteAllBytesAsync(sourceFile, content);

        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var (manager, _, _, pieceManagers) = CreateManager();
        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), null, false);
        await manager.StartAsync(session.Id);

        var pieceManager = pieceManagers[session.Id];
        var completed = pieceManager.OnBlockReceived(0, 0, content);

        Assert.Equal(0, completed);
        Assert.True(session.PieceCompletion[0]);
        Assert.Equal(1.0, session.ProgressFraction);
    }
}
