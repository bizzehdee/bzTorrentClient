using System.Net;
using System.Net.Sockets;
using bzTorrent;
using bzTorrent.IO;
using bzTorrentClient.Engine.Logging;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Accepts inbound peer connections on the listen port and routes each to the right torrent.
///
/// A peer that dials us sends its handshake first; that handshake carries the info-hash, which
/// is the only thing that says which torrent the connection is for - so this reads the peer's
/// handshake, looks up the matching torrent's <see cref="IPeerConnectionManager"/>, and hands
/// the connection over to it (which sends our half of the handshake and then serves/leeches).
///
/// TCP only: uTP (UDP) has no passive-open/accept, so the engine can't accept inbound uTP -
/// the UDP listen port is still worth forwarding for outbound uTP, but nothing is accepted on
/// it here. Best-effort throughout: a bind failure (port in use) is logged and the client
/// simply runs without accepting inbound peers.
/// </summary>
public sealed class InboundPeerListener : IDisposable
{
    // A peer that connects but never completes a handshake must not tie up an accept slot.
    private const int HandshakeReadTimeoutSeconds = 8;

    private readonly string _localPeerId;
    private readonly PeerEncryptionMode _encryptionMode;
    private readonly Func<string, IPeerConnectionManager?> _resolveByInfoHash;
    private readonly Func<IReadOnlyCollection<string>> _activeInfoHashes;
    private readonly IDebugLogger _logger;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <param name="resolveByInfoHash">Maps a received info-hash (40-char hex) to the connection manager for that torrent, or null if none is active.</param>
    /// <param name="activeInfoHashes">The info-hashes of all currently active torrents - registered on each accepted connection so MSE/PE can decrypt the obfuscated handshake.</param>
    public InboundPeerListener(
        string localPeerId,
        PeerEncryptionMode encryptionMode,
        Func<string, IPeerConnectionManager?> resolveByInfoHash,
        Func<IReadOnlyCollection<string>> activeInfoHashes,
        IDebugLogger? logger = null)
    {
        _localPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        _encryptionMode = encryptionMode;
        _resolveByInfoHash = resolveByInfoHash ?? throw new ArgumentNullException(nameof(resolveByInfoHash));
        _activeInfoHashes = activeInfoHashes ?? throw new ArgumentNullException(nameof(activeInfoHashes));
        _logger = logger ?? NullDebugLogger.Instance;
    }

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_cts is not null)
                return;

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
            catch (SocketException ex)
            {
                // Port already in use, insufficient privileges, etc. Not fatal - we just don't
                // accept inbound peers (outbound still works).
                _logger.Log($"Inbound: could not listen on port {port}: {ex.Message}");
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _logger.Log($"Inbound: accepting TCP peer connections on port {port}.");
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null)
                return;

            _cts.Cancel();
            _cts = null;

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Already torn down.
            }

            _listener = null;
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Listener stopped/disposed out from under us.
                return;
            }

            // Each peer runs on its own task - HandleConnection blocks for the whole connection.
            _ = Task.Run(() => HandleConnection(socket, cancellationToken), CancellationToken.None);
        }
    }

    private void HandleConnection(Socket socket, CancellationToken cancellationToken)
    {
        var remoteEndpoint = socket.RemoteEndPoint as IPEndPoint;

        var connection = new PeerWireConnection<TCPSocket>(new TCPSocket(socket))
        {
            Timeout = 30,
            EncryptionMode = _encryptionMode,
        };

        // MSE/PE obfuscates the handshake with the info-hash, so the receiving side must know
        // the candidate hashes up front to decrypt it - register every active torrent's.
        foreach (var infoHash in _activeInfoHashes())
            connection.EncryptionOptions.AddKnownInfoHash(infoHash);

        var client = new PeerWireClient(connection);

        try
        {
            // Pump until the peer's handshake arrives (revealing the info-hash) or we give up.
            var deadline = DateTime.UtcNow.AddSeconds(HandshakeReadTimeoutSeconds);
            while (!cancellationToken.IsCancellationRequested
                   && connection.RemoteHandshake is null
                   && DateTime.UtcNow < deadline)
            {
                if (!client.Process())
                {
                    PeerWireSafety.SafeDisconnect(client);
                    return;
                }

                Thread.Sleep(10);
            }

            var infoHash = connection.RemoteHandshake?.InfoHash;
            if (string.IsNullOrEmpty(infoHash) || remoteEndpoint is null)
            {
                PeerWireSafety.SafeDisconnect(client);
                return;
            }

            var manager = _resolveByInfoHash(infoHash);
            if (manager is null)
            {
                // Not a torrent we're currently running - nothing to hand it to.
                PeerWireSafety.SafeDisconnect(client);
                return;
            }

            _logger.Log($"Inbound: accepted peer {remoteEndpoint} for {infoHash}.");
            manager.AcceptInbound(client, remoteEndpoint); // runs the connection to completion
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            PeerWireSafety.SafeDisconnect(client);
        }
    }
}
