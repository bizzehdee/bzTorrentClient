using bzTorrent.Data;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Networking;
using bzTorrentClient.Engine.Tests.Persistence;
using bzTorrentClient.Engine.Tests.Testing;
using bzTorrentClient.Engine.Transfer;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class NetworkedSessionManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-networked-{Guid.NewGuid():N}");

    // Every manager CreateManager hands out, so Dispose can tear them all down at the end of
    // each test. A magnet StartAsync spins up a detached metadata-fetch retry loop that runs
    // until the manager is disposed (or the torrent stopped/removed); a test that leaves one
    // running leaks a loop which re-spawns 10 blocking worker tasks every retry interval,
    // starving the thread pool for later tests. On low-core Linux CI that starvation is
    // enough to push another test's fetch continuation past its polling deadline - which is
    // why those failures showed up on Linux but not Windows.
    private readonly List<NetworkedSessionManager> _managers = new();

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.Dispose();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private (NetworkedSessionManager manager, Dictionary<Guid, FakePeerSource> peerSources, Dictionary<Guid, FakePeerConnectionManager> connectionManagers, Dictionary<Guid, IPieceManager> pieceManagers)
        CreateManager() => CreateManager(new InMemorySessionStore());

    private (NetworkedSessionManager manager, Dictionary<Guid, FakePeerSource> peerSources, Dictionary<Guid, FakePeerConnectionManager> connectionManagers, Dictionary<Guid, IPieceManager> pieceManagers)
        CreateManager(
            InMemorySessionStore store,
            Guid? failingSessionId = null,
            TimeSpan? metadataRetryDelay = null,
            IDefaultTrackerListProvider? defaultTrackerListProvider = null,
            ClientSettings? settings = null,
            TimeSpan? seedingPolicyCheckInterval = null)
    {
        settings ??= new ClientSettings(_tempDir);
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
                if (session.Id == failingSessionId)
                    throw new InvalidOperationException("Simulated failure to resume this session.");

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
            metadataFetchTimeout: TimeSpan.FromMilliseconds(100),
            metadataRetryDelay: metadataRetryDelay ?? TimeSpan.FromMilliseconds(100),
            defaultTrackerListProvider: defaultTrackerListProvider,
            seedingPolicyCheckInterval: seedingPolicyCheckInterval);

        _managers.Add(manager);
        return (manager, peerSources, connectionManagers, pieceManagers);
    }

    private static TorrentAddSource.TorrentFile RealTorrentFileSource() =>
        new(File.ReadAllBytes(Path.Combine("TestFiles", "UbuntuTestTorrent.torrent")));

    private static TorrentAddSource.Magnet MagnetOnlySource(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task StartAsync_NonPrivateTorrent_UpsertsDefaultTrackersIntoMetadata()
    {
        Directory.CreateDirectory(_tempDir);
        var sourceFile = Path.Combine(_tempDir, "public-source.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 20).Select(b => (byte)b).ToArray());
        var publicMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        publicMetadata.Save(torrentBytes);

        var provider = new FakeDefaultTrackerListProvider("udp://new-default-tracker.example.com:1337/announce");

        var (manager, _, _, _) = CreateManager(new InMemorySessionStore(), defaultTrackerListProvider: provider);
        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, false);

        await manager.StartAsync(session.Id);

        Assert.Contains("udp://new-default-tracker.example.com:1337/announce", session.Metadata.AnnounceList);
    }

    [Fact]
    public async Task StartAsync_DefaultTrackerAlreadyInTorrentsOwnList_IsNotDuplicated()
    {
        var fakeMetadata = new FakeMetadata(pieceCount: 0);
        fakeMetadata.AnnounceList.Add("udp://existing.example.com:1337/announce");

        var provider = new FakeDefaultTrackerListProvider(
            "udp://existing.example.com:1337/announce",
            "udp://new-default-tracker.example.com:1337/announce");

        var store = new InMemorySessionStore();
        store.Seed(new TorrentSession(MagnetOnlySource(), fakeMetadata, _tempDir));

        var (manager, _, _, _) = CreateManager(store, defaultTrackerListProvider: provider);
        await manager.InitializeAsync();
        var session = manager.Sessions.Single();

        await manager.StartAsync(session.Id);

        Assert.Equal(1, session.Metadata.AnnounceList.Count(t => t == "udp://existing.example.com:1337/announce"));
        Assert.Contains("udp://new-default-tracker.example.com:1337/announce", session.Metadata.AnnounceList);
    }

    [Fact]
    public async Task StartAsync_PrivateTorrent_DoesNotAddDefaultTrackers()
    {
        var sourceFile = Path.Combine(_tempDir, "private-source.bin");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 20).Select(b => (byte)b).ToArray());
        var privateMetadata = Metadata.CreateFromPath(sourceFile, isPrivate: true);
        using var torrentBytes = new MemoryStream();
        privateMetadata.Save(torrentBytes);

        var provider = new FakeDefaultTrackerListProvider("udp://new-default-tracker.example.com:1337/announce");
        var (manager, _, _, _) = CreateManager(new InMemorySessionStore(), defaultTrackerListProvider: provider);
        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, false);

        await manager.StartAsync(session.Id);

        Assert.DoesNotContain("udp://new-default-tracker.example.com:1337/announce", session.Metadata.AnnounceList);
    }

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
        // for up to metadataFetchTimeout) — it runs detached, so poll for it to finish
        // rather than a fixed sleep: MetadataFetcher's worker count is high enough that
        // under full-suite thread-pool contention a short fixed wait can be flaky.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!connectionManagers[session.Id].Started && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20));

        // No peers were raised on the fake peer source, so the fetch times out quickly
        // and the session is left with its 0-piece stub metadata.
        Assert.Empty(session.Metadata.PieceHashes);
        Assert.True(connectionManagers[session.Id].Started);

        // The fetch retries forever until it succeeds or the torrent is stopped/removed —
        // dispose so this test doesn't leave a background retry loop running past its own
        // lifetime (Dispose tears down the runtime, which the loop checks for on its next
        // iteration and returns instead of retrying again).
        manager.Dispose();
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

    [Fact]
    public async Task SeedingPolicy_RatioReached_AutomaticallyStopsSeedingIntoCompleted()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "content.bin");
        var content = Enumerable.Range(0, 100).Select(b => (byte)b).ToArray();
        await File.WriteAllBytesAsync(sourceFile, content);

        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var settings = new ClientSettings(_tempDir) { SeedUntilRatio = 1.0, SeedUntilMinutes = 999 };
        var (manager, _, connectionManagers, pieceManagers) = CreateManager(
            new InMemorySessionStore(),
            settings: settings,
            seedingPolicyCheckInterval: TimeSpan.FromMilliseconds(30));

        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), null, false);
        await manager.StartAsync(session.Id);
        pieceManagers[session.Id].OnBlockReceived(0, 0, content);
        Assert.Equal(TorrentState.Seeding, session.State);

        // Simulate having downloaded the 100 bytes (ratio's denominator) and then
        // uploaded the same amount again, reaching a 1.0 ratio.
        connectionManagers[session.Id].BytesDownloaded = 100;
        connectionManagers[session.Id].BytesUploaded = 100;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.State == TorrentState.Seeding && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(TorrentState.Completed, session.State);
        Assert.True(session.SeedingLimitReached);
        Assert.Equal(1.0, session.SeedRatio);
        Assert.True(connectionManagers[session.Id].Disposed);
    }

    [Fact]
    public async Task SeedingPolicy_LimitNotYetReached_KeepsSeedingAndBanksTransferredBytes()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "content.bin");
        var content = Enumerable.Range(0, 100).Select(b => (byte)b).ToArray();
        await File.WriteAllBytesAsync(sourceFile, content);

        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var settings = new ClientSettings(_tempDir) { SeedUntilRatio = 10.0, SeedUntilMinutes = 999 };
        var (manager, _, connectionManagers, pieceManagers) = CreateManager(
            new InMemorySessionStore(),
            settings: settings,
            seedingPolicyCheckInterval: TimeSpan.FromMilliseconds(30));

        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), null, false);
        await manager.StartAsync(session.Id);
        pieceManagers[session.Id].OnBlockReceived(0, 0, content);

        connectionManagers[session.Id].BytesDownloaded = 100;
        connectionManagers[session.Id].BytesUploaded = 50;

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (session.TotalBytesUploaded < 50 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(TorrentState.Seeding, session.State);
        Assert.False(session.SeedingLimitReached);
        Assert.Equal(50, session.TotalBytesUploaded);
        Assert.Equal(100, session.TotalBytesDownloaded);
    }

    [Fact]
    public async Task InitializeAsync_ResumesSessionsThatWereActiveOnLastShutdown()
    {
        // Regression test: a torrent left Active when the app last closed used to stay
        // inert until the user clicked Start again — InitializeAsync only loaded session
        // records into memory, it never restarted their networking.
        var store = new InMemorySessionStore();

        var (firstRunManager, _, firstRunConnectionManagers, _) = CreateManager(store);
        var activeSession = await firstRunManager.AddAsync(RealTorrentFileSource(), null, startImmediately: true);
        var pausedSession = await firstRunManager.AddAsync(MagnetOnlySource("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), null, startImmediately: false);
        Assert.Equal(TorrentState.Active, activeSession.State);
        Assert.Equal(TorrentState.Paused, pausedSession.State);

        // Simulate an app restart: a fresh manager over the same persisted store.
        var (secondRunManager, _, secondRunConnectionManagers, _) = CreateManager(store);
        await secondRunManager.InitializeAsync();

        Assert.True(secondRunConnectionManagers[activeSession.Id].Started);
        Assert.False(secondRunConnectionManagers.ContainsKey(pausedSession.Id));
    }

    [Fact]
    public async Task InitializeAsync_ResumesSessionsThatWereCompletedOnLastShutdown()
    {
        // Regression test: a torrent left Completed (i.e. it was running and seeding) when
        // the app last closed should keep seeding across a restart without the user
        // needing to press Start again - only an explicit Stop should end that, same as an
        // Active torrent already gets. It resumes into Seeding, the running equivalent of
        // Completed, since it's now actually running again.
        Directory.CreateDirectory(_tempDir);
        var sourceFile = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 100).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);
        var torrentFileBytes = torrentBytes.ToArray();

        var store = new InMemorySessionStore();
        var completedSession = new TorrentSession(
            Guid.NewGuid(),
            new TorrentAddSource.TorrentFile(torrentFileBytes),
            Metadata.FromBuffer(torrentFileBytes),
            _tempDir,
            TorrentState.Completed,
            DateTime.UtcNow,
            new bool[1]);
        store.Seed(completedSession);

        var (manager, _, connectionManagers, _) = CreateManager(store);
        await manager.InitializeAsync();

        var resumed = manager.Sessions.Single();
        Assert.True(connectionManagers[resumed.Id].Started);
        Assert.Equal(TorrentState.Seeding, resumed.State);
    }

    [Fact]
    public async Task InitializeAsync_OneSessionFailingToResume_StillResumesTheOthers()
    {
        var store = new InMemorySessionStore();

        var (firstRunManager, _, _, _) = CreateManager(store);
        var goodSession = await firstRunManager.AddAsync(RealTorrentFileSource(), null, startImmediately: true);
        var badSession = await firstRunManager.AddAsync(MagnetOnlySource("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), null, startImmediately: true);

        var (secondRunManager, _, secondRunConnectionManagers, _) = CreateManager(
            store,
            failingSessionId: badSession.Id);
        await secondRunManager.InitializeAsync();

        Assert.True(secondRunConnectionManagers[goodSession.Id].Started);
        Assert.Equal(TorrentState.Error, secondRunManager.Sessions.Single(s => s.Id == badSession.Id).State);

        // firstRunManager's magnet session never found metadata, so it's still retrying
        // in the background — dispose so it doesn't outlive this test.
        firstRunManager.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_ResumedSessionHasCompleteDataOnDisk_SkipsRedownloadingIt()
    {
        // Regression test: PieceCompletion is only saved on lifecycle transitions, not as
        // pieces finish, so it can be stale across a restart even for a torrent that
        // actually finished downloading. Verification against disk must catch this.
        Directory.CreateDirectory(_tempDir);
        var sourceFile = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 100).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);
        var torrentFileBytes = torrentBytes.ToArray();

        var store = new InMemorySessionStore();
        var (firstRunManager, _, _, _) = CreateManager(store);
        var added = await firstRunManager.AddAsync(new TorrentAddSource.TorrentFile(torrentFileBytes), _tempDir, startImmediately: false);

        // Simulate the torrent having been Active (and fully downloaded) when the app last
        // closed, but its persisted completion bitfield never having been updated.
        var entity = await store.LoadAllAsync();
        var preExisting = entity.Single();
        var stillIncomplete = new TorrentSession(
            preExisting.Id,
            preExisting.Source,
            preExisting.Metadata,
            preExisting.DownloadDirectory,
            TorrentState.Active,
            preExisting.AddedAtUtc,
            new bool[preExisting.Metadata.PieceHashes.Count]);
        store.Seed(stillIncomplete);

        var (secondRunManager, _, secondRunConnectionManagers, secondRunPieceManagers) = CreateManager(store);
        await secondRunManager.InitializeAsync();

        var resumed = secondRunManager.Sessions.Single(s => s.Id == added.Id);
        Assert.True(resumed.PieceCompletion[0]);
        Assert.Equal(1.0, resumed.ProgressFraction);
        Assert.True(secondRunConnectionManagers[added.Id].Started);
        Assert.True(secondRunPieceManagers[added.Id].IsPieceComplete(0));
    }

    [Fact]
    public async Task InitializeAsync_PausedSessionHasCompleteDataOnDisk_ShowsAccurateProgressWithoutStarting()
    {
        // Regression test: what's downloaded must be known from the cached metadata the
        // moment the torrent list loads, not only once the user hits Start - a Paused
        // torrent (never auto-resumed) should still show accurate progress right away.
        Directory.CreateDirectory(_tempDir);
        var sourceFile = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 100).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var store = new InMemorySessionStore();
        var (firstRunManager, _, _, _) = CreateManager(store);
        var added = await firstRunManager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, startImmediately: false);
        Assert.Equal(TorrentState.Paused, added.State);

        var (secondRunManager, _, secondRunConnectionManagers, _) = CreateManager(store);
        await secondRunManager.InitializeAsync();

        var resumed = secondRunManager.Sessions.Single(s => s.Id == added.Id);
        Assert.True(resumed.PieceCompletion[0]);
        Assert.Equal(1.0, resumed.ProgressFraction);
        Assert.Equal(TorrentState.Completed, resumed.State);

        // Never started - no runtime/networking should have been spun up for it.
        Assert.False(secondRunConnectionManagers.ContainsKey(added.Id));
    }

    [Fact]
    public async Task StartAsync_StoppedSessionHasDataOnDisk_RecognizesItWithoutRedownloading()
    {
        // Same guarantee, but for a torrent started fresh mid-session (not via app-restart
        // resume) whose download directory already has matching data in it — e.g. it was
        // added and stopped earlier this run, or the files were placed there out-of-band.
        Directory.CreateDirectory(_tempDir);
        var sourceFile = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 100).Select(b => (byte)b).ToArray());
        var builtMetadata = Metadata.CreateFromPath(sourceFile);
        using var torrentBytes = new MemoryStream();
        builtMetadata.Save(torrentBytes);

        var (manager, _, connectionManagers, pieceManagers) = CreateManager();
        var session = await manager.AddAsync(new TorrentAddSource.TorrentFile(torrentBytes.ToArray()), _tempDir, startImmediately: true);

        Assert.True(session.PieceCompletion[0]);
        Assert.Equal(1.0, session.ProgressFraction);
        Assert.True(connectionManagers[session.Id].Started);
        Assert.True(pieceManagers[session.Id].IsPieceComplete(0));
    }

    [Fact]
    public async Task RunDeferredMetadataFetchAsync_KeepsRetryingAfterAFailedAttempt()
    {
        // Regression test: a magnet torrent's metadata fetch used to run exactly once —
        // if it failed (e.g. too few peers known that early), the torrent was stuck
        // showing "(fetching metadata…)" forever, even as the swarm kept growing via
        // ongoing tracker/DHT/PEX activity in the background. It must keep retrying.
        var (manager, peerSources, connectionManagers, _) = CreateManager(
            new InMemorySessionStore(),
            metadataRetryDelay: TimeSpan.FromMilliseconds(50));
        var session = await manager.AddAsync(MagnetOnlySource(), null, false);

        await manager.StartAsync(session.Id);

        // Each attempt subscribes to PeerFound exactly once; wait for at least a second
        // attempt to start, proving the loop didn't stop after the first failure. Also
        // wait for the stub connection manager to start (between-attempts, once the first
        // one fails) so the swarm can keep growing while later attempts try their luck.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((peerSources[session.Id].SubscribeCount < 2 || !connectionManagers[session.Id].Started) && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20));

        Assert.True(peerSources[session.Id].SubscribeCount >= 2);
        Assert.True(connectionManagers[session.Id].Started);

        manager.Dispose();
    }

    [Fact]
    public async Task RunDeferredMetadataFetchAsync_StopsRetryingOncePaused()
    {
        // Metadata is only ever fetched for an active torrent - pausing doesn't tear down
        // the runtime (Stop's job), so without an explicit state check the retry loop would
        // otherwise keep silently trying in the background even while shown as paused.
        var (manager, peerSources, _, _) = CreateManager(
            new InMemorySessionStore(),
            metadataRetryDelay: TimeSpan.FromMilliseconds(50));
        var session = await manager.AddAsync(MagnetOnlySource(), null, false);

        await manager.StartAsync(session.Id);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (peerSources[session.Id].SubscribeCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.True(peerSources[session.Id].SubscribeCount >= 2);

        await manager.PauseAsync(session.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(150)); // let any in-flight attempt settle
        var countAfterPause = peerSources[session.Id].SubscribeCount;

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(countAfterPause, peerSources[session.Id].SubscribeCount);
    }
}
