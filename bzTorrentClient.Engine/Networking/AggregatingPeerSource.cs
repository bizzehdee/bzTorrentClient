using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using bzTorrent;
using bzTorrent.Data;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// One instance per active torrent. Each instance runs its own DHT node and LAN-discovery
/// listener (rather than sharing one process-wide instance across torrents), since
/// bzTorrent's <c>DHTClient.PeerFound</c> doesn't tag which info-hash a peer was found for —
/// sharing one DHT client across concurrent torrent searches would make results ambiguous.
/// </summary>
public sealed class AggregatingPeerSource : IPeerSource
{
    private const int MaxConsecutiveTrackerFailures = 3;

    // Each tracker gets its own polling task, but the announce itself is a synchronous,
    // blocking network call - letting every tracker announce at once (a torrent can carry
    // dozens) both hammers the swarm on a single burst and, since each blocks a thread-pool
    // thread, starves the pool so the announces trickle out anyway. Cap how many announce at
    // the same time; the rest wait their turn without holding a thread.
    private const int MaxConcurrentAnnounces = 3;
    private readonly SemaphoreSlim _announceThrottle = new(MaxConcurrentAnnounces);

    private static readonly TimeSpan TrackerFailureRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinTrackerAnnounceInterval = TimeSpan.FromSeconds(30);

    private readonly IMetadata _metadata;
    private readonly int _listenPort;
    private readonly string _localPeerId;
    private readonly Func<string, ITrackerClient> _trackerClientFactory;
    private readonly Func<IDhtPeerFinder>? _dhtPeerFinderFactory;
    private readonly Func<ILanPeerFinder>? _lanPeerFinderFactory;
    private readonly TimeSpan _trackerFailureRetryDelay;

    private readonly HashSet<string> _seenPeers = new();
    private readonly object _seenPeersLock = new();

    /// <summary>Guards <see cref="IMetadata.AnnounceList"/> mutations - each tracker is polled from its own task, and the backing collection isn't thread-safe for concurrent removes.</summary>
    private readonly object _announceListLock = new();

    private CancellationTokenSource? _cts;
    private IDhtPeerFinder? _dhtPeerFinder;
    private ILanPeerFinder? _lanPeerFinder;
    private readonly List<Task> _trackerTasks = new();

    private int _trackerPeersFound;
    private int _dhtPeersFound;
    private int _lanPeersFound;

    private readonly ConcurrentDictionary<string, TrackerStatus> _trackerStatuses = new();

    public event Action<IPEndPoint>? PeerFound;

    public int TrackerPeersFound => Volatile.Read(ref _trackerPeersFound);
    public int DhtPeersFound => Volatile.Read(ref _dhtPeersFound);
    public int LanPeersFound => Volatile.Read(ref _lanPeersFound);
    public int DhtNodeCount => _dhtPeerFinder?.NodeCount ?? 0;
    public IReadOnlyCollection<TrackerStatus> TrackerStatuses => _trackerStatuses.Values.ToList();

    private readonly bool _enableDht;
    private readonly bool _enableLan;

    public AggregatingPeerSource(
        IMetadata metadata,
        int listenPort,
        string localPeerId,
        Func<string, ITrackerClient>? trackerClientFactory = null,
        Func<IDhtPeerFinder>? dhtPeerFinderFactory = null,
        Func<ILanPeerFinder>? lanPeerFinderFactory = null,
        TimeSpan? trackerFailureRetryDelay = null,
        bool enableDht = true,
        bool enableLan = true)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _listenPort = listenPort;
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _trackerClientFactory = trackerClientFactory ?? DefaultTrackerClientFactory;
        _dhtPeerFinderFactory = dhtPeerFinderFactory ?? (() => new DhtPeerFinder());
        _lanPeerFinderFactory = lanPeerFinderFactory ?? (() => new LanPeerFinder());
        _trackerFailureRetryDelay = trackerFailureRetryDelay ?? TrackerFailureRetryDelay;
        _enableDht = enableDht;
        _enableLan = enableLan;
    }

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();

        foreach (var tracker in _metadata.AnnounceList)
        {
            var url = tracker;
            _trackerTasks.Add(Task.Run(() => PollTrackerAsync(url, _cts.Token)));
        }

        // Private torrents (BEP-27) must only find peers via the tracker.
        if (!_metadata.Private)
        {
            // DHT/LAN discovery are best-effort extras on top of trackers — a broken network
            // stack (no multicast route, DHT socket bind failure, etc.) must degrade this
            // torrent to tracker-only peers, not crash the whole session/app.
            if (_enableDht)
            {
                try
                {
                    _dhtPeerFinder = _dhtPeerFinderFactory?.Invoke();
                    if (_dhtPeerFinder is not null)
                    {
                        _dhtPeerFinder.PeerFound += OnDhtPeerFound;
                        _dhtPeerFinder.StartSearch(_metadata.Hash);
                    }
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    _dhtPeerFinder = null;
                }
            }

            if (_enableLan)
            {
                try
                {
                    _lanPeerFinder = _lanPeerFinderFactory?.Invoke();
                    if (_lanPeerFinder is not null)
                    {
                        _lanPeerFinder.PeerFound += OnLanPeerFound;
                        _lanPeerFinder.Announce(_listenPort, _metadata.HashString);
                    }
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    _lanPeerFinder = null;
                }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        if (_dhtPeerFinder is not null)
        {
            _dhtPeerFinder.PeerFound -= OnDhtPeerFound;
            _dhtPeerFinder.Dispose();
            _dhtPeerFinder = null;
        }

        if (_lanPeerFinder is not null)
        {
            _lanPeerFinder.PeerFound -= OnLanPeerFound;
            _lanPeerFinder.Dispose();
            _lanPeerFinder = null;
        }

        _trackerTasks.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Surfaces a peer discovered elsewhere (e.g. via PEX on a live connection) through this
    /// source's <see cref="PeerFound"/> event, deduped the same as tracker/DHT/LAN peers, so
    /// every consumer - including the metadata fetcher - gets the benefit of PEX, not just the
    /// connection manager that happened to learn about it.
    /// </summary>
    public void NotePeer(IPEndPoint endpoint) => OnPeerFound(endpoint);

    private void OnDhtPeerFound(IPEndPoint endpoint)
    {
        Interlocked.Increment(ref _dhtPeersFound);
        OnPeerFound(endpoint);
    }

    private void OnLanPeerFound(IPEndPoint endpoint)
    {
        Interlocked.Increment(ref _lanPeersFound);
        OnPeerFound(endpoint);
    }

    private void OnPeerFound(IPEndPoint endpoint)
    {
        if (!IsUsablePeerEndpoint(endpoint))
            return;

        var key = $"{endpoint.Address}:{endpoint.Port}";
        lock (_seenPeersLock)
        {
            if (!_seenPeers.Add(key))
                return;
        }

        PeerFound?.Invoke(endpoint);
    }

    private async Task PollTrackerAsync(string tracker, CancellationToken cancellationToken)
    {
        ITrackerClient trackerClient;
        try
        {
            trackerClient = _trackerClientFactory(tracker);
        }
        catch
        {
            return;
        }

        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Bound how many trackers announce concurrently (see MaxConcurrentAnnounces). The
            // slot is held only for the blocking announce itself, not the long wait between
            // announces, so all trackers still get their turn promptly.
            try
            {
                await _announceThrottle.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            AnnounceInfo? announceInfo = null;
            try
            {
                announceInfo = trackerClient.Announce(new AnnounceRequest
                {
                    Url = tracker,
                    Hash = _metadata.HashString,
                    PeerId = _localPeerId,
                    BytesLeft = _metadata.PieceHashes.Count * _metadata.PieceSize,
                    Event = 0,
                    NumWant = 200,
                    ListenPort = _listenPort,
                });
            }
            catch
            {
                announceInfo = null;
            }
            finally
            {
                _announceThrottle.Release();
            }

            if (announceInfo is not null)
            {
                consecutiveFailures = 0;
                var peers = announceInfo.Peers.ToList();
                Interlocked.Add(ref _trackerPeersFound, peers.Count);
                foreach (var peer in peers)
                    OnPeerFound(peer);

                var priorPeersFound = _trackerStatuses.TryGetValue(tracker, out var priorStatus) ? priorStatus.PeersFound : 0;
                _trackerStatuses[tracker] = new TrackerStatus(
                    tracker,
                    PeersFound: priorPeersFound + peers.Count,
                    Seeders: announceInfo.Seeders,
                    Leechers: announceInfo.Leechers,
                    LastAnnounceUtc: DateTime.UtcNow,
                    LastError: null);

                var interval = TimeSpan.FromSeconds(Math.Max(announceInfo.WaitTime, (int)MinTrackerAnnounceInterval.TotalSeconds));
                if (!await AsyncUtil.TryDelay(interval, cancellationToken))
                    return;
            }
            else
            {
                consecutiveFailures++;

                _trackerStatuses[tracker] = _trackerStatuses.TryGetValue(tracker, out var lastGoodStatus)
                    ? lastGoodStatus with { LastError = "Announce failed" }
                    : new TrackerStatus(tracker, PeersFound: 0, Seeders: 0, Leechers: 0, LastAnnounceUtc: null, LastError: "Announce failed");

                if (consecutiveFailures >= MaxConsecutiveTrackerFailures)
                {
                    // A tracker that's failed this many times in a row for this session is
                    // dead weight - drop it from the torrent's own tracker list so it stops
                    // cluttering the Trackers UI and isn't retried again until the torrent is
                    // reloaded fresh (e.g. on the next app launch, from its original source).
                    lock (_announceListLock)
                    {
                        _metadata.AnnounceList.Remove(tracker);
                    }

                    _trackerStatuses.TryRemove(tracker, out _);
                    return;
                }

                if (!await AsyncUtil.TryDelay(_trackerFailureRetryDelay, cancellationToken))
                    return;
            }
        }
    }

    private static ITrackerClient DefaultTrackerClientFactory(string tracker) => tracker switch
    {
        _ when tracker.StartsWith("http", StringComparison.OrdinalIgnoreCase) => new HTTPTrackerClient(10),
        _ when tracker.StartsWith("udp", StringComparison.OrdinalIgnoreCase) => new UDPTrackerClient(10),
        _ => throw new NotSupportedException($"Unsupported tracker protocol: {tracker}")
    };

    private static bool IsUsablePeerEndpoint(IPEndPoint? peer)
    {
        if (peer is null || peer.Port is <= 0 or > 65535)
            return false;

        var address = peer.Address;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (IPAddress.Any.Equals(address) || IPAddress.Broadcast.Equals(address) || IPAddress.None.Equals(address))
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224;
    }
}
