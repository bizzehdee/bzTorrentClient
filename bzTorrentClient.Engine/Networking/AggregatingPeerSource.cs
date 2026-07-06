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
    private static readonly TimeSpan TrackerFailureRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinTrackerAnnounceInterval = TimeSpan.FromSeconds(30);

    private readonly IMetadata _metadata;
    private readonly int _listenPort;
    private readonly string _localPeerId;
    private readonly Func<string, ITrackerClient> _trackerClientFactory;
    private readonly Func<IDhtPeerFinder>? _dhtPeerFinderFactory;
    private readonly Func<ILanPeerFinder>? _lanPeerFinderFactory;

    private readonly HashSet<string> _seenPeers = new();
    private readonly object _seenPeersLock = new();

    private CancellationTokenSource? _cts;
    private IDhtPeerFinder? _dhtPeerFinder;
    private ILanPeerFinder? _lanPeerFinder;
    private readonly List<Task> _trackerTasks = new();

    private int _trackerPeersFound;
    private int _dhtPeersFound;
    private int _lanPeersFound;

    public event Action<IPEndPoint>? PeerFound;

    public int TrackerPeersFound => Volatile.Read(ref _trackerPeersFound);
    public int DhtPeersFound => Volatile.Read(ref _dhtPeersFound);
    public int LanPeersFound => Volatile.Read(ref _lanPeersFound);
    public int DhtNodeCount => _dhtPeerFinder?.NodeCount ?? 0;

    public AggregatingPeerSource(
        IMetadata metadata,
        int listenPort,
        string localPeerId,
        Func<string, ITrackerClient>? trackerClientFactory = null,
        Func<IDhtPeerFinder>? dhtPeerFinderFactory = null,
        Func<ILanPeerFinder>? lanPeerFinderFactory = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _listenPort = listenPort;
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _trackerClientFactory = trackerClientFactory ?? DefaultTrackerClientFactory;
        _dhtPeerFinderFactory = dhtPeerFinderFactory ?? (() => new DhtPeerFinder());
        _lanPeerFinderFactory = lanPeerFinderFactory ?? (() => new LanPeerFinder());
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

            if (announceInfo is not null)
            {
                consecutiveFailures = 0;
                var peers = announceInfo.Peers.ToList();
                Interlocked.Add(ref _trackerPeersFound, peers.Count);
                foreach (var peer in peers)
                    OnPeerFound(peer);

                var interval = TimeSpan.FromSeconds(Math.Max(announceInfo.WaitTime, (int)MinTrackerAnnounceInterval.TotalSeconds));
                if (!await AsyncUtil.TryDelay(interval, cancellationToken))
                    return;
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveTrackerFailures)
                    return;

                if (!await AsyncUtil.TryDelay(TrackerFailureRetryDelay, cancellationToken))
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
