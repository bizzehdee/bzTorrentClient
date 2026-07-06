using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using bzTorrent;
using bzTorrent.Data;
using bzTorrent.IO;
using bzTorrent.ProtocolExtensions;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Transfer;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Owns up to <c>maxConnectionsPerTorrent</c> outbound <see cref="IPeerWireClient"/>
/// connections for one torrent. No inbound listener is implemented (MVP is
/// outbound-only), so "seeding" only happens to peers this instance itself connected
/// to for downloading — a peer that requests a block we already have gets served over
/// that same connection, but we never accept fresh inbound connections.
/// </summary>
public sealed class PeerConnectionManager : IPeerConnectionManager
{
    private const int MaxRequestsInFlight = 10;
    private const int HandshakeTimeoutSeconds = 8;
    private static readonly TimeSpan PexBroadcastInterval = TimeSpan.FromSeconds(30);

    private readonly IMetadata _metadata;
    private readonly ITorrentStorage _storage;
    private readonly IPieceManager _pieceManager;
    private readonly string _localPeerId;
    private readonly int _maxConnectionsPerTorrent;
    private readonly Func<int, bool> _tryReserveConnections;
    private readonly Action<int> _releaseConnections;

    private readonly ConcurrentQueue<IPEndPoint> _candidates = new();
    private readonly HashSet<string> _knownPeers = new();
    private readonly object _knownPeersLock = new();
    private readonly ConcurrentDictionary<int, IPeerWireClient> _activeClients = new();
    private readonly ConcurrentDictionary<int, IPEndPoint> _activeEndpoints = new();
    private int _nextPeerId;
    private int _pexPeersFound;
    private long _bytesDownloaded;
    private long _bytesUploaded;

    private CancellationTokenSource? _cts;

    public PeerConnectionManager(
        IMetadata metadata,
        ITorrentStorage storage,
        IPieceManager pieceManager,
        string localPeerId,
        int maxConnectionsPerTorrent,
        Func<int, bool> tryReserveConnections,
        Action<int> releaseConnections)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _maxConnectionsPerTorrent = maxConnectionsPerTorrent;
        _tryReserveConnections = tryReserveConnections ?? throw new ArgumentNullException(nameof(tryReserveConnections));
        _releaseConnections = releaseConnections ?? throw new ArgumentNullException(nameof(releaseConnections));
    }

    public int ActiveConnectionCount => _activeClients.Count;

    public IReadOnlyCollection<IPEndPoint> ConnectedEndpoints => _activeEndpoints.Values.ToList();

    public int PexPeersFound => Volatile.Read(ref _pexPeersFound);
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);

    public void AddPeerCandidate(IPEndPoint endpoint)
    {
        var key = $"{endpoint.Address}:{endpoint.Port}";
        lock (_knownPeersLock)
        {
            if (!_knownPeers.Add(key))
                return;
        }

        _candidates.Enqueue(endpoint);
    }

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => DispatchLoopAsync(_cts.Token));
    }

    public void Pause()
    {
        _cts?.Cancel();
        _cts = null;

        foreach (var client in _activeClients.Values)
            PeerWireSafety.SafeDisconnect(client);
    }

    public void Stop()
    {
        Pause();
        _candidates.Clear();
        lock (_knownPeersLock)
        {
            _knownPeers.Clear();
        }
    }

    public void Dispose() => Stop();

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_activeClients.Count >= _maxConnectionsPerTorrent || !_candidates.TryDequeue(out var endpoint))
            {
                if (!await AsyncUtil.TryDelay(TimeSpan.FromMilliseconds(200), cancellationToken))
                    return;
                continue;
            }

            if (!_tryReserveConnections(1))
            {
                _candidates.Enqueue(endpoint);
                if (!await AsyncUtil.TryDelay(TimeSpan.FromMilliseconds(500), cancellationToken))
                    return;
                continue;
            }

            var peerId = Interlocked.Increment(ref _nextPeerId);
            _ = Task.Run(() => RunPeerConnection(peerId, endpoint, cancellationToken), cancellationToken);
        }
    }

    private void RunPeerConnection(int peerId, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var choked = true;
        var inflight = 0;
        var peerBitfield = new bool[_metadata.PieceHashes.Count];
        var connectedAt = DateTime.UtcNow;

        var connection = new PeerWireConnection<TCPSocket> { Timeout = 30, EncryptionMode = PeerEncryptionMode.PlainText };
        var client = new PeerWireClient(connection) { KeepConnectionAlive = true };
        _activeClients[peerId] = client;

        client.HandshakeComplete += pwc =>
        {
            SendOurBitfield(pwc);
            pwc.SendInterested();
        };

        client.BitField += (pwc, _, bitfield) =>
        {
            Array.Copy(bitfield, peerBitfield, Math.Min(bitfield.Length, peerBitfield.Length));
            _pieceManager.RegisterPeerBitfield(peerId, peerBitfield);
            pwc.SendInterested();
        };

        client.Have += (_, index) =>
        {
            if (index >= 0 && index < peerBitfield.Length)
                peerBitfield[index] = true;
            _pieceManager.RegisterPeerHave(peerId, index);
        };

        client.UnChoke += _ => choked = false;
        client.Choke += _ => choked = true;
        client.Interested += pwc => pwc.SendUnChoke();

        client.Request += (pwc, index, start, length) =>
        {
            if (!_pieceManager.IsPieceComplete(index))
                return;

            try
            {
                var pieceData = _storage.ReadPiece(index);
                if (start < 0 || start + length > pieceData.Length)
                    return;

                var block = new byte[length];
                Array.Copy(pieceData, start, block, 0, length);
                pwc.SendPiece((uint)index, (uint)start, block);
                Interlocked.Add(ref _bytesUploaded, block.Length);
            }
            catch (IOException)
            {
                // Peer asked for a block we can no longer read; just skip serving it.
            }
        };

        client.Piece += (_, index, start, buffer) =>
        {
            inflight = Math.Max(0, inflight - 1);
            Interlocked.Add(ref _bytesDownloaded, buffer.Length);
            var completedPiece = _pieceManager.OnBlockReceived(index, start, buffer);
            if (completedPiece is not null)
                BroadcastHave(completedPiece.Value);
        };

        // Peer Exchange (BEP-11): learn about peers our own peers already know about, and
        // reciprocate — a real swarm sees more peers this way than tracker+DHT alone.
        // Skipped for private torrents (BEP-27): peers may only be found via the tracker.
        UTPeerExchange? pex = null;
        if (!_metadata.Private)
        {
            pex = new UTPeerExchange();
            pex.Added += (_, _, pexEndpoint, _) =>
            {
                Interlocked.Increment(ref _pexPeersFound);
                AddPeerCandidate(pexEndpoint);
            };

            var extendedProtocol = new ExtendedProtocolExtensions();
            extendedProtocol.RegisterProtocolExtension(client, pex);
            client.RegisterBTExtension(extendedProtocol);
        }

        try
        {
            client.Connect(endpoint);
            _activeEndpoints[peerId] = endpoint;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            CleanUp(peerId);
            return;
        }

        try
        {
            client.Handshake(_metadata.HashString, _localPeerId);
            var lastPexBroadcast = DateTime.MinValue;

            while (!cancellationToken.IsCancellationRequested && client.Process())
            {
                if (!client.ReceivedHandshake && connectedAt < DateTime.UtcNow.AddSeconds(-HandshakeTimeoutSeconds))
                {
                    PeerWireSafety.SafeDisconnect(client);
                    break;
                }

                if (!choked && inflight < MaxRequestsInFlight)
                {
                    var request = _pieceManager.TryGetNextRequest(peerId, peerBitfield);
                    if (request is not null)
                    {
                        client.SendRequest((uint)request.PieceIndex, (uint)request.BlockOffset, (uint)request.Length);
                        inflight++;
                    }
                }

                if (pex is not null && client.ReceivedHandshake && DateTime.UtcNow - lastPexBroadcast > PexBroadcastInterval)
                {
                    lastPexBroadcast = DateTime.UtcNow;
                    BroadcastPex(client, pex, endpoint);
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // Peer connection dropped; fall through to cleanup below.
        }
        finally
        {
            PeerWireSafety.SafeDisconnect(client);
            CleanUp(peerId);
        }
    }

    private void CleanUp(int peerId)
    {
        _activeClients.TryRemove(peerId, out _);
        _activeEndpoints.TryRemove(peerId, out _);
        _pieceManager.UnregisterPeer(peerId);
        _releaseConnections(1);
    }

    private void SendOurBitfield(IPeerWireClient client)
    {
        var pieceCount = _metadata.PieceHashes.Count;
        if (pieceCount == 0)
            return;

        var bitfield = new bool[pieceCount];
        var haveAny = false;
        for (var i = 0; i < pieceCount; i++)
        {
            if (!_pieceManager.IsPieceComplete(i))
                continue;
            bitfield[i] = true;
            haveAny = true;
        }

        if (haveAny)
            client.SendBitField(bitfield);
    }

    private void BroadcastHave(int pieceIndex)
    {
        foreach (var peer in _activeClients.Values)
            peer.SendHave((uint)pieceIndex);
    }

    /// <summary>
    /// Tells one connected peer about the other peers we're currently connected to.
    /// Simplified vs. a full PEX implementation (no "dropped" tracking, and peers we've
    /// already told may get re-announced) — harmless since the receiving side already
    /// dedupes candidates, and far simpler than tracking per-connection announce history.
    /// </summary>
    private void BroadcastPex(IPeerWireClient client, UTPeerExchange pex, IPEndPoint excludeEndpoint)
    {
        var knownPeers = _activeEndpoints.Values
            .Where(e => !e.Equals(excludeEndpoint))
            .Take(50)
            .ToArray();

        if (knownPeers.Length > 0)
            pex.SendMessage(client, knownPeers, null, null);
    }
}
