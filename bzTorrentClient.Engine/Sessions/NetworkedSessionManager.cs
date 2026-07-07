using System.Net;
using bzTorrent.Data;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Transfer;

namespace bzTorrentClient.Engine.Sessions;

/// <summary>
/// Decorates a plain <see cref="ISessionManager"/> (state/persistence only) with the
/// actual networking: starting a torrent spins up its <see cref="IPeerSource"/> and
/// <see cref="IPeerConnectionManager"/>; pausing disconnects peers but keeps them
/// running behind the scenes for candidates (soft halt); stopping tears both down
/// entirely (hard halt). See PLAN.md for the Paused-vs-Stopped rationale.
/// </summary>
public sealed class NetworkedSessionManager : ISessionManager, ITorrentRuntimeInfoProvider, ITwoPhaseSessionInitializer, IDisposable
{
    private static readonly TimeSpan DefaultMetadataFetchTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// One fetch attempt only covers whatever peers the swarm happened to hand the
    /// PeerSource by the time it ran — if it fails, retrying later against a swarm that's
    /// kept growing (via ongoing tracker re-announces, DHT, and PEX) is far more likely to
    /// eventually find a peer that actually has the full metadata.
    /// </summary>
    private static readonly TimeSpan DefaultMetadataRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _inner;
    private readonly IClientSettings _settings;
    private readonly string _localPeerId;
    private readonly Func<TorrentSession, int, IPeerSource> _peerSourceFactory;
    private readonly Func<TorrentSession, ITorrentStorage, IPieceManager, IPeerConnectionManager> _connectionManagerFactory;
    private readonly TimeSpan _metadataFetchTimeout;
    private readonly TimeSpan _metadataRetryDelay;

    // Shared across every torrent's connection manager, so the configured limit is a true
    // global cap rather than per-torrent. Reads the current setting on every call (see
    // TokenBucketRateLimiter), so changing it in the Settings dialog takes effect live.
    private readonly IRateLimiter _downloadLimiter;
    private readonly IRateLimiter _uploadLimiter;

    private readonly Dictionary<Guid, TorrentRuntime> _runtimes = new();
    private readonly object _runtimesLock = new();

    // A torrent's cached metadata (piece hashes) is what makes verification possible in
    // the first place, regardless of whether it came from a .torrent file up front or a
    // magnet/BTIH fetch resolved later — once known, disk state only needs hashing once
    // per process run. Guarded by _runtimesLock rather than a separate lock since it's
    // touched from exactly the same call sites.
    private readonly HashSet<Guid> _verifiedSessionIds = new();

    private readonly IDefaultTrackerListProvider _defaultTrackerListProvider;

    private static readonly TimeSpan DefaultSeedingPolicyCheckInterval = TimeSpan.FromSeconds(5);
    private readonly Timer _seedingPolicyTimer;

    public NetworkedSessionManager(
        ISessionManager inner,
        IClientSettings settings,
        string localPeerId,
        Func<TorrentSession, int, IPeerSource>? peerSourceFactory = null,
        Func<TorrentSession, ITorrentStorage, IPieceManager, IPeerConnectionManager>? connectionManagerFactory = null,
        TimeSpan? metadataFetchTimeout = null,
        TimeSpan? metadataRetryDelay = null,
        IDefaultTrackerListProvider? defaultTrackerListProvider = null,
        TimeSpan? seedingPolicyCheckInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _peerSourceFactory = peerSourceFactory ?? DefaultPeerSourceFactory;
        _connectionManagerFactory = connectionManagerFactory ?? DefaultConnectionManagerFactory;
        _metadataFetchTimeout = metadataFetchTimeout ?? DefaultMetadataFetchTimeout;
        _metadataRetryDelay = metadataRetryDelay ?? DefaultMetadataRetryDelay;
        _defaultTrackerListProvider = defaultTrackerListProvider ?? NullDefaultTrackerListProvider.Instance;
        _downloadLimiter = new TokenBucketRateLimiter(() => _settings.GlobalDownloadLimitBytesPerSecond);
        _uploadLimiter = new TokenBucketRateLimiter(() => _settings.GlobalUploadLimitBytesPerSecond);

        // Enforces the seed-until-time/ratio settings and periodically banks transferred
        // bytes onto each session - independent of the UI's own refresh timer, so the
        // policy still applies even though nothing currently reads GetNetworkStats.
        var checkInterval = seedingPolicyCheckInterval ?? DefaultSeedingPolicyCheckInterval;
        _seedingPolicyTimer = new Timer(
            _ => _ = ApplySeedingPolicyAsync(CancellationToken.None),
            null,
            checkInterval,
            checkInterval);
    }

    public IReadOnlyCollection<TorrentSession> Sessions => _inner.Sessions;
    public int GlobalConnectionBudget => _inner.GlobalConnectionBudget;
    public int ReservedConnections => _inner.ReservedConnections;

    /// <summary>Full readiness in one call - equivalent to <see cref="LoadAsync"/> followed by <see cref="ResumeAsync"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        await ResumeAsync(cancellationToken);
    }

    /// <summary>Just loads persisted sessions into memory - a DB read, no network calls or disk hashing. Fast enough to await before showing a UI.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    /// <summary>
    /// The slow part of startup: refreshes the default tracker list, verifies every
    /// loaded session's on-disk data against its piece hashes, and auto-resumes torrents
    /// that were Active/Completed when the app last closed. Safe to run after a UI has
    /// already shown whatever <see cref="LoadAsync"/> loaded - sessions are mutated in
    /// place, so a periodic refresh picks up the corrected state as it lands.
    /// </summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        // Best-effort - refreshed before any torrent's runtime is built below, so the
        // upserted list is as current as this launch can make it, but a slow/failed fetch
        // must not stop the rest of startup (RefreshAsync swallows its own failures).
        await _defaultTrackerListProvider.RefreshAsync(cancellationToken);

        var sessions = _inner.Sessions.ToList();

        // A torrent left Active - or already Completed and seeding - when the app last
        // closed should resume without the user having to click Start again: otherwise
        // every restart silently halts transfers, and a finished torrent stops seeding
        // the moment the app restarts even though nothing told it to stop. Snapshot which
        // ones qualify *before* verifying: verification mutates these same TorrentSession
        // objects' State in place, and a previously-running torrent that turns out to no
        // longer have all its data on disk (e.g. files removed externally) ends up Paused
        // by it, not Active, but should still resume downloading the missing pieces same
        // as if it had never finished.
        var previouslyRunningIds = sessions
            .Where(s => s.State is TorrentState.Active or TorrentState.Completed or TorrentState.Seeding)
            .Select(s => s.Id)
            .ToList();

        // A torrent's cached metadata (its piece hashes) is what tells us what's already
        // downloaded, regardless of whether it was added as a .torrent file, a magnet
        // link, or a raw BTIH — the source only mattered for how that metadata was first
        // acquired. Verifying every loaded torrent against disk here, before the list is
        // ever shown, means downloaded/missing state is already accurate the moment it's
        // displayed rather than only once the user hits Start. One session failing to
        // verify (disk error, ...) must not stop the rest.
        foreach (var session in sessions)
        {
            try
            {
                await EnsureVerifiedAsync(session, cancellationToken);
            }
            catch (Exception ex)
            {
                session.Fail(ex.Message);
            }
        }

        foreach (var sessionId in previouslyRunningIds)
        {
            try
            {
                await StartAsync(sessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                sessions.First(s => s.Id == sessionId).Fail(ex.Message);
            }
        }
    }

    public async Task<TorrentSession> AddAsync(TorrentAddSource source, string? downloadDirectory, bool startImmediately, CancellationToken cancellationToken = default)
    {
        // Never let the inner manager auto-start: its AddAsync calls its own StartAsync,
        // which would bypass this decorator's networking setup entirely. Start via our
        // own StartAsync afterward instead, so the peer source/connection manager get created.
        var session = await _inner.AddAsync(source, downloadDirectory, startImmediately: false, cancellationToken);

        if (startImmediately)
            await StartAsync(session.Id, cancellationToken);

        return session;
    }

    public bool TryReserveConnections(int count) => _inner.TryReserveConnections(count);
    public void ReleaseConnections(int count) => _inner.ReleaseConnections(count);
    public Task SaveAsync(Guid sessionId, CancellationToken cancellationToken = default) => _inner.SaveAsync(sessionId, cancellationToken);

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var existingSession = _inner.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (existingSession is not null)
            await EnsureVerifiedAsync(existingSession, cancellationToken);

        await _inner.StartAsync(sessionId, cancellationToken);
        var session = _inner.Sessions.First(s => s.Id == sessionId);

        var runtime = GetOrCreateRuntime(session);
        runtime.PeerSource.Start();

        if (session.Metadata.PieceHashes.Count == 0 && session.Metadata is Metadata concreteMetadata)
        {
            // Fetching metadata for a magnet/info-hash torrent can take up to
            // _metadataFetchTimeout (default 90s). Awaiting it here would keep the
            // UI's Start command "running" for that whole window — since the button
            // ties its enabled state to that, it'd stay looking disabled long after
            // the user had already moved on (e.g. clicked Stop). Run it detached
            // instead; the connection manager starts once it resolves either way.
            _ = RunDeferredMetadataFetchAsync(session, runtime, concreteMetadata, cancellationToken);
        }
        else
        {
            runtime.ConnectionManager.Start();
        }
    }

    private async Task RunDeferredMetadataFetchAsync(TorrentSession session, TorrentRuntime runtime, Metadata concreteMetadata, CancellationToken cancellationToken)
    {
        var fetched = false;
        var stubConnectionManagerStarted = false;

        while (!fetched)
        {
            try
            {
                fetched = await MetadataFetcher.TryFetchAsync(concreteMetadata, runtime.PeerSource, _localPeerId, _metadataFetchTimeout, cancellationToken);
            }
            catch (Exception)
            {
                // Best-effort background fetch — a failure here has no caller to observe it.
            }

            lock (_runtimesLock)
            {
                // The torrent may have been stopped/removed while the fetch was running —
                // don't keep retrying (or resurrect) a runtime nobody's using any more.
                if (!_runtimes.TryGetValue(session.Id, out var currentRuntime) || currentRuntime != runtime)
                    return;
            }

            if (fetched)
                break;

            // Pausing doesn't tear down the runtime (that's Stop's job), so the check
            // above alone wouldn't catch it — metadata is only ever fetched for an active
            // torrent; a paused one should stop retrying until the user starts it again,
            // at which point StartAsync spins up a fresh fetch loop.
            if (session.State != TorrentState.Active)
                return;

            if (!stubConnectionManagerStarted)
            {
                // Let peers keep connecting (and exchanging PEX) between attempts, instead
                // of leaving the swarm frozen at whatever the first attempt saw — this is
                // exactly how a torrent ends up with dozens of connected peers but no
                // metadata forever without a retry loop.
                runtime.ConnectionManager.Start();
                stubConnectionManagerStarted = true;
            }

            if (!await AsyncUtil.TryDelay(_metadataRetryDelay, cancellationToken))
                return;
        }

        try
        {
            bool stillRelevant;
            lock (_runtimesLock)
            {
                stillRelevant = _runtimes.TryGetValue(session.Id, out var currentRuntime) && currentRuntime == runtime;
            }

            if (!stillRelevant)
                return;

            session.OnMetadataPopulated();

            // Cache the resolved metadata against the torrent's info-hash the moment it's
            // known, same as a .torrent-file add already gets for free — from here on,
            // what's downloaded is determined from this cached metadata regardless of how
            // the torrent was originally added. Promote before verifying so the single
            // save below persists metadata, completion, and state together.
            using (var stream = new MemoryStream())
            {
                concreteMetadata.Save(stream);
                session.PromoteSourceToTorrentFile(stream.ToArray());
            }

            // Piece hashes only become known once metadata is fetched, so this is the
            // earliest a magnet torrent's on-disk data (e.g. re-adding a magnet for
            // content already downloaded elsewhere) can be checked against them.
            await EnsureVerifiedAsync(session, cancellationToken);

            lock (_runtimesLock)
            {
                if (!_runtimes.TryGetValue(session.Id, out var currentRuntime) || currentRuntime != runtime)
                    return;

                runtime.Storage.EnsureAllocated();
                BuildPieceAndConnectionManagers(session, runtime);
                runtime.ConnectionManager.Start();
            }
        }
        catch (Exception)
        {
            // Detached background continuation — nothing left to propagate this to.
        }
    }

    public async Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _inner.PauseAsync(sessionId, cancellationToken);

        TorrentRuntime? runtime;
        lock (_runtimesLock)
        {
            _runtimes.TryGetValue(sessionId, out runtime);
        }

        runtime?.ConnectionManager.Pause();
    }

    public async Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _inner.StopAsync(sessionId, cancellationToken);
        TearDownRuntime(sessionId);
    }

    public async Task RemoveAsync(Guid sessionId, bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        TearDownRuntime(sessionId);

        lock (_runtimesLock)
        {
            _verifiedSessionIds.Remove(sessionId);
        }

        await _inner.RemoveAsync(sessionId, deleteFiles, cancellationToken);
    }

    public void Dispose()
    {
        _seedingPolicyTimer.Dispose();

        lock (_runtimesLock)
        {
            foreach (var runtime in _runtimes.Values)
                runtime.Dispose();
            _runtimes.Clear();
        }
    }

    /// <summary>
    /// Banks newly-transferred bytes onto every session with a live runtime, and stops any
    /// Seeding session that's hit its seed-until-time or seed-until-ratio limit. Runs on a
    /// background timer, independent of whether anything is currently reading network
    /// stats - a single session's failure here must not stop the rest from being checked.
    /// </summary>
    private async Task ApplySeedingPolicyAsync(CancellationToken cancellationToken)
    {
        List<(TorrentSession Session, TorrentRuntime Runtime)> snapshot;
        lock (_runtimesLock)
        {
            snapshot = _inner.Sessions
                .Select(s => (Session: s, Runtime: _runtimes.GetValueOrDefault(s.Id)))
                .Where(t => t.Runtime is not null)
                .Select(t => (t.Session, Runtime: t.Runtime!))
                .ToList();
        }

        foreach (var (session, runtime) in snapshot)
        {
            try
            {
                var transferred = SampleTransferBytes(session, runtime);

                if (session.State == TorrentState.Seeding && !session.SeedingLimitReached)
                {
                    var limitReached = session.TotalSeedingElapsed >= TimeSpan.FromMinutes(_settings.SeedUntilMinutes)
                        || (session.SeedRatio is { } ratio && ratio >= _settings.SeedUntilRatio);

                    if (limitReached)
                    {
                        session.StopSeedingDueToLimit();
                        TearDownRuntime(session.Id);
                        await _inner.SaveAsync(session.Id, cancellationToken);
                        continue;
                    }
                }

                // Persist newly-transferred bytes as they happen (rather than only at the
                // next lifecycle transition) so an unclean shutdown loses at most one
                // check interval's worth of progress toward the seed-until-ratio target.
                if (transferred)
                    await _inner.SaveAsync(session.Id, cancellationToken);
            }
            catch (Exception)
            {
                // Best-effort background policy tick.
            }
        }
    }

    /// <summary>Adds the delta since the last sample to the session's all-time transfer totals. Returns whether anything actually moved.</summary>
    private static bool SampleTransferBytes(TorrentSession session, TorrentRuntime runtime)
    {
        var uploaded = runtime.ConnectionManager.BytesUploaded;
        var downloaded = runtime.ConnectionManager.BytesDownloaded;

        var deltaUploaded = Math.Max(0, uploaded - runtime.LastSampledBytesUploaded);
        var deltaDownloaded = Math.Max(0, downloaded - runtime.LastSampledBytesDownloaded);

        runtime.LastSampledBytesUploaded = uploaded;
        runtime.LastSampledBytesDownloaded = downloaded;

        if (deltaUploaded == 0 && deltaDownloaded == 0)
            return false;

        session.AddTransferredBytes(deltaUploaded, deltaDownloaded);
        return true;
    }

    public int GetActiveConnectionCount(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime) ? runtime.ConnectionManager.ActiveConnectionCount : 0;
        }
    }

    public IReadOnlyCollection<PeerConnectionInfo> GetConnectedPeers(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime)
                ? runtime.ConnectionManager.ConnectedPeers
                : Array.Empty<PeerConnectionInfo>();
        }
    }

    public IReadOnlyCollection<TrackerStatus> GetTrackerStatuses(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime)
                ? runtime.PeerSource.TrackerStatuses
                : Array.Empty<TrackerStatus>();
        }
    }

    public TorrentNetworkStats GetNetworkStats(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            if (!_runtimes.TryGetValue(sessionId, out var runtime))
                return TorrentNetworkStats.Empty;

            return new TorrentNetworkStats(
                ActiveConnections: runtime.ConnectionManager.ActiveConnectionCount,
                BytesDownloaded: runtime.ConnectionManager.BytesDownloaded,
                BytesUploaded: runtime.ConnectionManager.BytesUploaded,
                TrackerPeersFound: runtime.PeerSource.TrackerPeersFound,
                DhtPeersFound: runtime.PeerSource.DhtPeersFound,
                LanPeersFound: runtime.PeerSource.LanPeersFound,
                PexPeersFound: runtime.ConnectionManager.PexPeersFound,
                DhtNodeCount: runtime.PeerSource.DhtNodeCount);
        }
    }

    /// <summary>
    /// Verifies a session against disk at most once per process run (tracked via
    /// <see cref="_verifiedSessionIds"/>, keyed by session id but really standing in for
    /// the torrent's info-hash — the actual cache key for its metadata) and persists the
    /// result. A no-op if piece hashes aren't known yet (a magnet/BTIH add whose metadata
    /// hasn't been fetched), or if this session was already verified this run.
    /// </summary>
    private async Task EnsureVerifiedAsync(TorrentSession session, CancellationToken cancellationToken)
    {
        if (session.Metadata.PieceHashes.Count == 0)
            return;

        lock (_runtimesLock)
        {
            if (_verifiedSessionIds.Contains(session.Id))
                return;
        }

        await VerifyAgainstDiskAsync(session, cancellationToken);

        lock (_runtimesLock)
        {
            _verifiedSessionIds.Add(session.Id);
        }

        await _inner.SaveAsync(session.Id, cancellationToken);
    }

    /// <summary>
    /// The persisted PieceCompletion bitfield is only saved on explicit lifecycle
    /// transitions (add/start/pause/stop), not as pieces finish during a run, so it can be
    /// stale by the time a torrent's runtime is (re)built. Re-derive real completion by
    /// hashing whatever's already on disk against the torrent's piece hashes (cached as
    /// part of the session's metadata), so only pieces actually missing get fetched.
    /// </summary>
    private static async Task VerifyAgainstDiskAsync(TorrentSession session, CancellationToken cancellationToken)
    {
        // Completed/Seeding count as "running" here same as Active - a finished torrent
        // keeps seeding until the user explicitly stops it, so if disk state has since
        // regressed (e.g. files removed externally) it must resume downloading the
        // missing pieces rather than just sit there having lost its running status.
        var wasRunning = session.State is TorrentState.Active or TorrentState.Completed or TorrentState.Seeding;

        session.Stop(); // Normalizes any prior state to Stopped, from which BeginChecking is always legal.
        session.BeginChecking();

        var storage = new FileSystemTorrentStorage(session.Metadata, session.DownloadDirectory);
        var completion = await Task.Run(() => PieceVerifier.Verify(session.Metadata, storage), cancellationToken);

        session.ApplyVerificationResult(completion);
        session.FinishChecking();

        // FinishChecking only ever lands on Paused or Completed. If this torrent was
        // running before the check started, restore it (Paused -> Active/Seeding,
        // Completed -> Seeding, per Start()) so it keeps going - callers
        // (StartAsync/RunDeferredMetadataFetchAsync) don't separately re-issue Start()
        // for a session that's already running mid-flight.
        if (wasRunning && session.State is TorrentState.Paused or TorrentState.Completed)
            session.Start();
    }

    private TorrentRuntime GetOrCreateRuntime(TorrentSession session)
    {
        lock (_runtimesLock)
        {
            if (_runtimes.TryGetValue(session.Id, out var existing))
                return existing;

            ApplyDefaultTrackers(session);

            var storage = new FileSystemTorrentStorage(session.Metadata, session.DownloadDirectory);
            storage.EnsureAllocated();

            var peerSource = _peerSourceFactory(session, _settings.ListenPort);
            var runtime = new TorrentRuntime(storage, peerSource);
            BuildPieceAndConnectionManagers(session, runtime);

            _runtimes[session.Id] = runtime;
            return runtime;
        }
    }

    /// <summary>
    /// Upserts the configured default tracker list (URL + text box, combined) into this
    /// torrent's own tracker list. The torrent's own trackers always take priority (this
    /// only adds ones it doesn't already have); skipped entirely for private torrents,
    /// which per BEP-27 must only announce to trackers named in their own metadata.
    /// </summary>
    private void ApplyDefaultTrackers(TorrentSession session)
    {
        if (session.Metadata.Private)
            return;

        var defaultTrackers = _defaultTrackerListProvider.GetTrackers();
        if (defaultTrackers.Count == 0)
            return;

        var existing = new HashSet<string>(session.Metadata.AnnounceList, StringComparer.OrdinalIgnoreCase);
        foreach (var tracker in defaultTrackers)
        {
            if (existing.Add(tracker))
                session.Metadata.AnnounceList.Add(tracker);
        }
    }

    /// <summary>
    /// (Re)builds the piece/connection managers for the current metadata. Called once
    /// up front, and again after a magnet/info-hash session's metadata is fetched — the
    /// first pass's managers were built against a 0-piece stub and must be replaced
    /// rather than patched, since <see cref="PeerConnectionManager"/> captures its
    /// <see cref="IPieceManager"/> at construction. Must be called under <see cref="_runtimesLock"/>.
    /// </summary>
    private void BuildPieceAndConnectionManagers(TorrentSession session, TorrentRuntime runtime)
    {
        var pieceManager = new RarestFirstPieceManager(session.Metadata, runtime.Storage, session.PieceCompletion);
        var connectionManager = _connectionManagerFactory(session, runtime.Storage, pieceManager);

        if (runtime.ConnectionManager is not null)
        {
            runtime.PeerSource.PeerFound -= runtime.ConnectionManager.AddPeerCandidate;

            // A magnet torrent's first connection manager may already be running against
            // the 0-piece stub (started while metadata kept retrying in the background) —
            // dispose it so its dispatch loop and any live connections don't leak once
            // replaced. No-op if it was never started.
            runtime.ConnectionManager.Dispose();
        }

        // The piece manager clones session.PieceCompletion at construction and tracks
        // completion internally from then on — without this, a piece finishing and
        // verifying never reaches the session, so progress/"downloaded" stay stuck at
        // 0% and Completed is never reached, even while data is genuinely arriving.
        pieceManager.PieceCompleted += session.MarkPieceVerified;

        runtime.PieceManager = pieceManager;
        runtime.ConnectionManager = connectionManager;
        runtime.PeerSource.PeerFound += connectionManager.AddPeerCandidate;
    }

    private void TearDownRuntime(Guid sessionId)
    {
        TorrentRuntime? runtime;
        lock (_runtimesLock)
        {
            if (!_runtimes.Remove(sessionId, out runtime))
                return;
        }

        runtime.Dispose();
    }

    private IPeerSource DefaultPeerSourceFactory(TorrentSession session, int listenPort) =>
        new AggregatingPeerSource(
            session.Metadata,
            listenPort,
            _localPeerId,
            enableDht: _settings.EnableDht,
            enableLan: _settings.EnableLpd);

    private IPeerConnectionManager DefaultConnectionManagerFactory(TorrentSession session, ITorrentStorage storage, IPieceManager pieceManager) =>
        new PeerConnectionManager(
            session.Metadata,
            storage,
            pieceManager,
            _localPeerId,
            _settings.MaxConnectionsPerTorrent,
            TryReserveConnections,
            ReleaseConnections,
            _downloadLimiter,
            _uploadLimiter,
            enablePex: _settings.EnablePex);

    private sealed class TorrentRuntime : IDisposable
    {
        public ITorrentStorage Storage { get; }
        public IPeerSource PeerSource { get; }
        public IPieceManager PieceManager { get; set; } = null!;
        public IPeerConnectionManager ConnectionManager { get; set; } = null!;

        /// <summary>Baseline for <see cref="NetworkedSessionManager.SampleTransferBytes"/> to diff against, since ConnectionManager's own counters reset each time a runtime is rebuilt.</summary>
        public long LastSampledBytesUploaded { get; set; }
        public long LastSampledBytesDownloaded { get; set; }

        public TorrentRuntime(ITorrentStorage storage, IPeerSource peerSource)
        {
            Storage = storage;
            PeerSource = peerSource;
        }

        public void Dispose()
        {
            ConnectionManager?.Dispose();
            PeerSource.Dispose();
        }
    }
}
