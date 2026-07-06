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
public sealed class NetworkedSessionManager : ISessionManager, ITorrentRuntimeInfoProvider, IDisposable
{
    private static readonly TimeSpan DefaultMetadataFetchTimeout = TimeSpan.FromSeconds(90);

    private readonly ISessionManager _inner;
    private readonly IClientSettings _settings;
    private readonly string _localPeerId;
    private readonly Func<TorrentSession, int, IPeerSource> _peerSourceFactory;
    private readonly Func<TorrentSession, ITorrentStorage, IPieceManager, IPeerConnectionManager> _connectionManagerFactory;
    private readonly TimeSpan _metadataFetchTimeout;

    private readonly Dictionary<Guid, TorrentRuntime> _runtimes = new();
    private readonly object _runtimesLock = new();

    public NetworkedSessionManager(
        ISessionManager inner,
        IClientSettings settings,
        string localPeerId,
        Func<TorrentSession, int, IPeerSource>? peerSourceFactory = null,
        Func<TorrentSession, ITorrentStorage, IPieceManager, IPeerConnectionManager>? connectionManagerFactory = null,
        TimeSpan? metadataFetchTimeout = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _peerSourceFactory = peerSourceFactory ?? DefaultPeerSourceFactory;
        _connectionManagerFactory = connectionManagerFactory ?? DefaultConnectionManagerFactory;
        _metadataFetchTimeout = metadataFetchTimeout ?? DefaultMetadataFetchTimeout;
    }

    public IReadOnlyCollection<TorrentSession> Sessions => _inner.Sessions;
    public int GlobalConnectionBudget => _inner.GlobalConnectionBudget;
    public int ReservedConnections => _inner.ReservedConnections;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

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

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
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
        try
        {
            fetched = await MetadataFetcher.TryFetchAsync(concreteMetadata, runtime.PeerSource, _localPeerId, _metadataFetchTimeout, cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort background fetch — a failure here has no caller to observe
            // it; the torrent just stays a metadata-less stub until retried.
        }

        try
        {
            lock (_runtimesLock)
            {
                // The torrent may have been stopped/removed while the fetch was running —
                // don't resurrect a runtime nobody's using any more.
                if (!_runtimes.TryGetValue(session.Id, out var currentRuntime) || currentRuntime != runtime)
                    return;

                if (fetched)
                {
                    session.OnMetadataPopulated();
                    runtime.Storage.EnsureAllocated();
                    BuildPieceAndConnectionManagers(session, runtime);
                }

                runtime.ConnectionManager.Start();
            }

            if (fetched)
            {
                using var stream = new MemoryStream();
                concreteMetadata.Save(stream);
                session.PromoteSourceToTorrentFile(stream.ToArray());
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

    public async Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        TearDownRuntime(sessionId);
        await _inner.RemoveAsync(sessionId, cancellationToken);
    }

    public void Dispose()
    {
        lock (_runtimesLock)
        {
            foreach (var runtime in _runtimes.Values)
                runtime.Dispose();
            _runtimes.Clear();
        }
    }

    public int GetActiveConnectionCount(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime) ? runtime.ConnectionManager.ActiveConnectionCount : 0;
        }
    }

    public IReadOnlyCollection<IPEndPoint> GetConnectedPeers(Guid sessionId)
    {
        lock (_runtimesLock)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime)
                ? runtime.ConnectionManager.ConnectedEndpoints
                : Array.Empty<IPEndPoint>();
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

    private TorrentRuntime GetOrCreateRuntime(TorrentSession session)
    {
        lock (_runtimesLock)
        {
            if (_runtimes.TryGetValue(session.Id, out var existing))
                return existing;

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
            runtime.PeerSource.PeerFound -= runtime.ConnectionManager.AddPeerCandidate;

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
        new AggregatingPeerSource(session.Metadata, listenPort, _localPeerId);

    private IPeerConnectionManager DefaultConnectionManagerFactory(TorrentSession session, ITorrentStorage storage, IPieceManager pieceManager) =>
        new PeerConnectionManager(
            session.Metadata,
            storage,
            pieceManager,
            _localPeerId,
            _settings.MaxConnectionsPerTorrent,
            TryReserveConnections,
            ReleaseConnections);

    private sealed class TorrentRuntime : IDisposable
    {
        public ITorrentStorage Storage { get; }
        public IPeerSource PeerSource { get; }
        public IPieceManager PieceManager { get; set; } = null!;
        public IPeerConnectionManager ConnectionManager { get; set; } = null!;

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
