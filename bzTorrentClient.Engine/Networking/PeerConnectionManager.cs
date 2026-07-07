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

    /// <summary>Used only to size a request against the download rate limiter before we know the real block length (almost always exactly this, per BEP-3 convention).</summary>
    private const int TypicalBlockSize = 16 * 1024;

    private static readonly TimeSpan PexBroadcastInterval = TimeSpan.FromSeconds(30);

    private readonly IMetadata _metadata;
    private readonly ITorrentStorage _storage;
    private readonly IPieceManager _pieceManager;
    private readonly string _localPeerId;
    private readonly int _maxConnectionsPerTorrent;
    private readonly Func<int, bool> _tryReserveConnections;
    private readonly Action<int> _releaseConnections;
    private readonly IRateLimiter _downloadLimiter;
    private readonly IRateLimiter _uploadLimiter;
    private readonly bool _enablePex;
    private readonly PeerEncryptionMode _encryptionMode;

    private readonly ConcurrentQueue<IPEndPoint> _candidates = new();
    private readonly HashSet<string> _knownPeers = new();
    private readonly object _knownPeersLock = new();
    private readonly ConcurrentDictionary<int, IPeerWireClient> _activeClients = new();
    private readonly ConcurrentDictionary<int, IPEndPoint> _activeEndpoints = new();
    private readonly ConcurrentDictionary<int, PeerByteCounters> _peerByteCounters = new();
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
        Action<int> releaseConnections,
        IRateLimiter? downloadLimiter = null,
        IRateLimiter? uploadLimiter = null,
        bool enablePex = true,
        PeerEncryptionMode encryptionMode = PeerEncryptionMode.PreferEncryption)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _maxConnectionsPerTorrent = maxConnectionsPerTorrent;
        _tryReserveConnections = tryReserveConnections ?? throw new ArgumentNullException(nameof(tryReserveConnections));
        _releaseConnections = releaseConnections ?? throw new ArgumentNullException(nameof(releaseConnections));
        _downloadLimiter = downloadLimiter ?? new TokenBucketRateLimiter(() => 0);
        _uploadLimiter = uploadLimiter ?? new TokenBucketRateLimiter(() => 0);
        _enablePex = enablePex;
        _encryptionMode = encryptionMode;
    }

    public int ActiveConnectionCount => _activeClients.Count;

    public IReadOnlyCollection<PeerConnectionInfo> ConnectedPeers => _activeEndpoints
        .Select(kvp =>
        {
            var counters = _peerByteCounters.GetOrAdd(kvp.Key, _ => new PeerByteCounters());
            var isEncrypted = _activeClients.TryGetValue(kvp.Key, out var client) && client.IsEncrypted;
            return new PeerConnectionInfo(
                kvp.Value,
                Interlocked.Read(ref counters.BytesDownloaded),
                Interlocked.Read(ref counters.BytesUploaded),
                counters.Transport,
                isEncrypted);
        })
        .ToList();

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
        // Most of the swarm is reachable over plain TCP, so try that first. A peer TCP
        // can't even connect to (e.g. behind a NAT/firewall that passes outbound UDP but
        // blocks unsolicited inbound TCP SYNs) gets one retry over uTP (BEP-29) before
        // being given up on entirely.
        if (RunPeerConnectionOverTransport<TCPSocket>(peerId, endpoint, cancellationToken))
            return;

        if (RunPeerConnectionOverTransport<UTPConnection>(peerId, endpoint, cancellationToken))
            return;

        CleanUp(peerId, endpoint);
    }

    /// <returns>
    /// False only when the connection itself never came up at all - the one case worth
    /// retrying over a different transport. True in every other case (connected and ran
    /// until disconnect/cancellation), whether or not the peer turned out to be useless;
    /// cleanup has already run internally for that case, same as before this was split
    /// out per-transport.
    /// </returns>
    private bool RunPeerConnectionOverTransport<TSocket>(int peerId, IPEndPoint endpoint, CancellationToken cancellationToken)
        where TSocket : ISocket, new()
    {
        var choked = true;
        var inflight = 0;
        var peerBitfield = new bool[_metadata.PieceHashes.Count];
        var connectedAt = DateTime.UtcNow;

        // A large share of real-world peers require or strongly prefer MSE/PE-encrypted
        // connections (many ISPs throttle/block plaintext BitTorrent traffic) — PlainText
        // here meant every such peer's handshake silently went nowhere: peers were found
        // (tracker/DHT/PEX all work independently of this), but nothing ever downloaded.
        // PreferEncryption (the default) negotiates MSE when the peer supports it and falls
        // back to plaintext when it doesn't, same default most mature clients use; the
        // encryption mode setting lets the user require it or disable it outright instead.
        var connection = new PeerWireConnection<TSocket> { Timeout = 30, EncryptionMode = _encryptionMode };
        var client = new PeerWireClient(connection) { KeepConnectionAlive = true };
        _activeClients[peerId] = client;

        var transport = typeof(TSocket) == typeof(UTPConnection) ? PeerTransportKind.Utp : PeerTransportKind.Tcp;

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

            // Over the upload budget: just drop this request. The peer will either
            // re-request later or move on to another source — there's no persistent
            // state here to corrupt by declining, unlike the download side.
            if (!_uploadLimiter.TryConsume(length))
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
                Interlocked.Add(ref _peerByteCounters.GetOrAdd(peerId, _ => new PeerByteCounters()).BytesUploaded, block.Length);
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
            Interlocked.Add(ref _peerByteCounters.GetOrAdd(peerId, _ => new PeerByteCounters()).BytesDownloaded, buffer.Length);
            var completedPiece = _pieceManager.OnBlockReceived(index, start, buffer);
            if (completedPiece is not null)
                BroadcastHave(completedPiece.Value);
        };

        // Peer Exchange (BEP-11): learn about peers our own peers already know about, and
        // reciprocate — a real swarm sees more peers this way than tracker+DHT alone.
        // Skipped for private torrents (BEP-27): peers may only be found via the tracker.
        UTPeerExchange? pex = null;
        if (!_metadata.Private && _enablePex)
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
            _peerByteCounters.GetOrAdd(peerId, _ => new PeerByteCounters()).Transport = transport;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // Didn't even connect - leave peerId's registration alone (no handshake ever
            // happened, nothing to unwind) so the caller can retry it over another transport.
            return false;
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

                // Checked before TryGetNextRequest, not after: that call marks whatever
                // block it returns as requested, so a block declined here for lack of
                // download budget would never get re-offered — it'd just silently stall.
                if (!choked && inflight < MaxRequestsInFlight && _downloadLimiter.TryConsume(TypicalBlockSize))
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
            CleanUp(peerId, endpoint);
        }

        return true;
    }

    /// <summary>
    /// Also forgets <paramref name="endpoint"/> from <see cref="_knownPeers"/> - without this,
    /// a peer that failed to connect or later disconnected could never be re-added as a
    /// candidate, even though the tracker/DHT/PEX will keep re-announcing it every interval.
    /// Left in place, that permanently shrinks the usable candidate pool over a long-running
    /// session as every peer that's ever been tried (successfully or not) becomes unreachable.
    /// </summary>
    private void CleanUp(int peerId, IPEndPoint endpoint)
    {
        _activeClients.TryRemove(peerId, out _);
        _activeEndpoints.TryRemove(peerId, out _);
        _peerByteCounters.TryRemove(peerId, out _);
        _pieceManager.UnregisterPeer(peerId);
        _releaseConnections(1);

        lock (_knownPeersLock)
        {
            _knownPeers.Remove($"{endpoint.Address}:{endpoint.Port}");
        }
    }

    private sealed class PeerByteCounters
    {
        public long BytesDownloaded;
        public long BytesUploaded;
        public PeerTransportKind Transport;
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
