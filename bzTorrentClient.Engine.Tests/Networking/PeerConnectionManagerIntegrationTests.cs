using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using bzTorrent.Data;
using bzTorrent.IO;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Storage;
using bzTorrentClient.Engine.Tests.Testing;
using bzTorrentClient.Engine.Transfer;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

/// <summary>
/// End-to-end over real loopback TCP (127.0.0.1) — no real bootstrap nodes/trackers
/// involved. The "seeder" is a hand-rolled raw-socket peer (not bzTorrent's own
/// PeerWireListener/Accept path), so this exercises only bzTorrentClient's own code
/// plus the outbound <c>PeerWireClient</c> path it actually uses in production.
/// </summary>
public class PeerConnectionManagerIntegrationTests : IDisposable
{
    private const string InfoHashHex = "0123456789abcdef0123456789abcdef01234567";
    private const string LeecherPeerId = "-bz0001-leecher00000";

    private readonly string _leecherDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-leech-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_leecherDir))
            Directory.Delete(_leecherDir, recursive: true);
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_DownloadsSinglePieceFromSeeder()
    {
        var pieceData = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pieceHash = SHA1.HashData(pieceData);

        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: pieceData.Length,
            pieceHashes: new[] { pieceHash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = pieceData.Length } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => RunRawSeederAsync(tcpListener, pieceData));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { });

        connectionManager.Start();
        connectionManager.AddPeerCandidate(new IPEndPoint(IPAddress.Loopback, port));

        var completed = await SpinUntilAsync(() => pieceManager.IsComplete, TimeSpan.FromSeconds(10));

        Assert.Equal(1, connectionManager.ActiveConnectionCount);
        var peer = Assert.Single(connectionManager.ConnectedPeers, p => p.EndPoint.Port == port);
        Assert.Equal(pieceData.Length, connectionManager.BytesDownloaded);
        Assert.Equal(pieceData.Length, peer.BytesDownloaded);
        Assert.Equal(PeerTransportKind.Tcp, peer.Transport);

        connectionManager.Stop();
        tcpListener.Stop();

        Assert.True(completed, "Expected the single piece to be downloaded and verified within the timeout.");
        var downloaded = File.ReadAllBytes(Path.Combine(_leecherDir, "payload.bin"));
        Assert.Equal(pieceData, downloaded);
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_BlocklistedIp_NeverConnects()
    {
        var pieceData = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pieceHash = SHA1.HashData(pieceData);

        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: pieceData.Length,
            pieceHashes: new[] { pieceHash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = pieceData.Length } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => RunRawSeederAsync(tcpListener, pieceData));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);

        var cacheFilePath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-blocklistcache-{Guid.NewGuid():N}.txt");
        var settings = new ClientSettings("/downloads") { IpBlocklistText = "127.0.0.1" };
        var blocklist = new IpBlocklistProvider(settings, cacheFilePath);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { },
            ipBlocklist: blocklist);

        connectionManager.Start();
        connectionManager.AddPeerCandidate(new IPEndPoint(IPAddress.Loopback, port));

        // Give a real (non-blocklisted) connection attempt ample time to have connected by now.
        await Task.Delay(500);

        connectionManager.Stop();
        tcpListener.Stop();
        File.Delete(cacheFilePath);

        Assert.Equal(0, connectionManager.ActiveConnectionCount);
        Assert.Empty(connectionManager.ConnectedPeers);
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_RequireEncryption_NeverDownloadsFromAPlaintextOnlySeeder()
    {
        // Proves the encryption mode setting actually reaches the wire: the same plaintext-only
        // seeder that PeerConnectionManager_DownloadsSinglePieceFromSeeder downloads from
        // successfully under the default (PreferEncryption) must never complete a download
        // when the connection manager is configured to require encryption instead.
        var pieceData = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pieceHash = SHA1.HashData(pieceData);

        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: pieceData.Length,
            pieceHashes: new[] { pieceHash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = pieceData.Length } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => RunRawSeederAsync(tcpListener, pieceData));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { },
            encryptionMode: PeerEncryptionMode.RequireEncryption);

        connectionManager.Start();
        connectionManager.AddPeerCandidate(new IPEndPoint(IPAddress.Loopback, port));

        var completed = await SpinUntilAsync(() => pieceManager.IsComplete, TimeSpan.FromSeconds(3));

        connectionManager.Stop();
        tcpListener.Stop();

        Assert.False(completed, "A RequireEncryption connection manager must not fall back to plaintext.");
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_DownloadLimiter_PacesRequests()
    {
        // Real end-to-end proof the download rate limiter actually slows a transfer down,
        // not just a unit test of TokenBucketRateLimiter in isolation. Three 16 KiB blocks
        // (RarestFirstPieceManager.BlockSize) capped at 16 KiB/s should take a few seconds,
        // not the sub-second it'd take unthrottled over loopback.
        const int blockSize = RarestFirstPieceManager.BlockSize;
        var pieceData = new byte[blockSize * 3];
        new Random(42).NextBytes(pieceData);
        var pieceHash = SHA1.HashData(pieceData);

        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: pieceData.Length,
            pieceHashes: new[] { pieceHash },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = pieceData.Length } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => RunRawSeederAsync(tcpListener, pieceData, requestsToServe: 3));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);
        var downloadLimiter = new TokenBucketRateLimiter(() => blockSize);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { },
            downloadLimiter: downloadLimiter);

        var startedAt = DateTime.UtcNow;
        connectionManager.Start();
        connectionManager.AddPeerCandidate(new IPEndPoint(IPAddress.Loopback, port));

        var completed = await SpinUntilAsync(() => pieceManager.IsComplete, TimeSpan.FromSeconds(20));
        var elapsed = DateTime.UtcNow - startedAt;

        connectionManager.Stop();
        tcpListener.Stop();

        Assert.True(completed, "Expected all three blocks to be downloaded and verified within the timeout.");
        // The bucket starts empty, so even the first of three same-rate blocks must wait
        // for a refill — comfortably over a second is a safe, non-flaky lower bound that
        // an unthrottled loopback transfer (normally tens of milliseconds) would never hit.
        Assert.True(elapsed >= TimeSpan.FromSeconds(1), $"Expected the rate limit to slow the download down; only took {elapsed.TotalMilliseconds}ms.");
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_HandshakeNotYetComplete_PeerIsNotInConnectedPeers()
    {
        // Regression test: a peer used to appear in ConnectedPeers (and therefore the UI's
        // Peers tab) the instant the transport-level Connect() call returned - for uTP in
        // particular that happens as soon as the SYN is sent, well before the connection is
        // actually established, so peers showed up long before (and sometimes without ever)
        // completing a real handshake, with no activity to show for it. Now a peer must
        // complete the BitTorrent handshake before it's considered "connected" for display.
        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: 32,
            pieceHashes: new[] { new byte[20] },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = 32 } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        using var releaseHandshake = new ManualResetEventSlim(false);
        var seederTask = Task.Run(() => RunHandshakeStallingSeederAsync(tcpListener, releaseHandshake));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { });

        connectionManager.Start();
        connectionManager.AddPeerCandidate(new IPEndPoint(IPAddress.Loopback, port));

        // Give the TCP connect itself plenty of time to complete while the seeder is still
        // deliberately withholding its handshake reply.
        await Task.Delay(500);
        Assert.Empty(connectionManager.ConnectedPeers);

        releaseHandshake.Set();
        var appeared = await SpinUntilAsync(() => connectionManager.ConnectedPeers.Count > 0, TimeSpan.FromSeconds(5));

        connectionManager.Stop();
        tcpListener.Stop();
        await seederTask;

        Assert.True(appeared, "Expected the peer to appear in ConnectedPeers once the handshake actually completed.");
    }

    [Fact(Timeout = 60000)]
    public async Task PeerConnectionManager_PeerDisconnectsThenReAdded_IsAllowedToReconnect()
    {
        var metadata = new FakeMetadata(
            pieceCount: 1,
            hashHex: InfoHashHex,
            pieceSize: 32,
            pieceHashes: new[] { new byte[20] },
            files: new[] { new MetadataFileInfo { Id = 0, Filename = "payload.bin", FileStartByte = 0, FileSize = 32 } });

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        var acceptCount = 0;
        _ = Task.Run(() => RunDisconnectingSeederAsync(tcpListener, () => Interlocked.Increment(ref acceptCount)));

        var storage = new FileSystemTorrentStorage(metadata, _leecherDir);
        storage.EnsureAllocated();
        var pieceManager = new RarestFirstPieceManager(metadata, storage);

        using var connectionManager = new PeerConnectionManager(
            metadata,
            storage,
            pieceManager,
            LeecherPeerId,
            maxConnectionsPerTorrent: 5,
            tryReserveConnections: _ => true,
            releaseConnections: _ => { });

        connectionManager.Start();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        connectionManager.AddPeerCandidate(endpoint);

        // Peer accepts, then immediately closes the connection - our own client should
        // notice the disconnect and clean itself up.
        var disconnected = await SpinUntilAsync(() => connectionManager.ActiveConnectionCount == 0 && Volatile.Read(ref acceptCount) >= 1, TimeSpan.FromSeconds(10));
        Assert.True(disconnected, "Expected the first connection to be accepted and then cleaned up after the peer disconnected.");

        // The same endpoint must be re-connectable - a peer that dropped once shouldn't be
        // permanently unreachable for the rest of the session (e.g. re-announced by PEX/tracker/DHT later).
        connectionManager.AddPeerCandidate(endpoint);
        var reconnected = await SpinUntilAsync(() => Volatile.Read(ref acceptCount) >= 2, TimeSpan.FromSeconds(10));

        connectionManager.Stop();
        tcpListener.Stop();

        Assert.True(reconnected, "Expected the endpoint to be retried after being re-added as a candidate.");
    }

    /// <summary>Accepts connections, completes the handshake, then disconnects immediately - simulating a peer that drops right away.</summary>
    private static async Task RunDisconnectingSeederAsync(TcpListener listener, Action onAccepted)
    {
        while (true)
        {
            TcpClient candidate;
            try
            {
                candidate = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var client = candidate;
            using var stream = client.GetStream();

            var incomingHandshake = new byte[68];
            if (!await TryReadExactAsync(stream, incomingHandshake))
            {
                continue;
            }

            onAccepted();

            var infoHash = new byte[20];
            Array.Copy(incomingHandshake, 28, infoHash, 0, 20);
            await stream.WriteAsync(BuildHandshake(infoHash, "-bz0001-seeder000000"));
            // Disconnect immediately - no bitfield, no data.
        }
    }

    /// <summary>Minimal BEP-3 peer: handshake, send an all-ones bitfield, unchoke on Interested, serve one Request.</summary>
    private static Task RunRawSeederAsync(TcpListener listener, byte[] pieceData) =>
        RunRawSeederAsync(listener, pieceData, requestsToServe: 1);

    /// <summary>
    /// Minimal BEP-3 peer: handshake, send an all-ones bitfield, unchoke on Interested,
    /// serve up to <paramref name="requestsToServe"/> Requests. Plaintext-only - like a
    /// real legacy peer, a connection that opens with anything other than a plaintext
    /// BitTorrent handshake (i.e. our own PreferEncryption client's MSE offer) is just
    /// closed rather than answered, so the client's plaintext fallback gets a prompt
    /// "connection closed" instead of hanging until its socket timeout.
    /// </summary>
    private static async Task RunRawSeederAsync(TcpListener listener, byte[] pieceData, int requestsToServe)
    {
        TcpClient client;
        NetworkStream stream;
        var incomingHandshake = new byte[68];

        while (true)
        {
            var candidate = await listener.AcceptTcpClientAsync();
            var candidateStream = candidate.GetStream();

            if (!await TryReadExactAsync(candidateStream, incomingHandshake.AsMemory(0, 1)))
            {
                candidate.Dispose();
                continue;
            }

            if (incomingHandshake[0] != 19)
            {
                candidate.Dispose();
                continue;
            }

            await ReadExactAsync(candidateStream, incomingHandshake.AsMemory(1, 67));
            client = candidate;
            stream = candidateStream;
            break;
        }

        using var __ = client;
        using var _ = stream;

        var infoHash = new byte[20];
        Array.Copy(incomingHandshake, 28, infoHash, 0, 20);
        await stream.WriteAsync(BuildHandshake(infoHash, "-bz0001-seeder000000"));

        await stream.WriteAsync(BuildMessage(5, new byte[] { 0x80 })); // bitfield: piece 0 set

        var requestsServed = 0;
        while (requestsServed < requestsToServe)
        {
            var lengthBuffer = new byte[4];
            if (!await TryReadExactAsync(stream, lengthBuffer))
                return;

            var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
            if (length == 0)
                continue; // keep-alive

            var body = new byte[length];
            await ReadExactAsync(stream, body);
            var messageId = body[0];

            if (messageId == 2) // interested
            {
                await stream.WriteAsync(BuildMessage(1, Array.Empty<byte>())); // unchoke
            }
            else if (messageId == 6) // request
            {
                var index = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1, 4));
                var begin = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5, 4));
                var reqLength = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(9, 4));

                var payload = new byte[8 + reqLength];
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), index);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), begin);
                Array.Copy(pieceData, (int)begin, payload, 8, (int)reqLength);

                await stream.WriteAsync(BuildMessage(7, payload));
                requestsServed++;
            }
        }

        // Keep the connection open after serving all requests so the leecher's peer stays
        // "connected" while the test inspects ActiveConnectionCount / ConnectedPeers, instead of
        // racing an immediate socket close - which the leecher's teardown wins on a slow /
        // thread-starved CI runner, dropping the peer before the assertions run. Drain until the
        // client hangs up at test teardown (connectionManager.Stop() / listener.Stop()).
        var drain = new byte[256];
        try
        {
            while (await stream.ReadAsync(drain) > 0)
            {
            }
        }
        catch
        {
            // Client closed or the listener was stopped - nothing left to serve.
        }
    }

    /// <summary>
    /// Plaintext-only, like <see cref="RunRawSeederAsync(TcpListener, byte[], int)"/> (any
    /// connection that doesn't open with a plaintext BT handshake is closed, so the client's
    /// PreferEncryption fallback gets a prompt "connection closed" and retries plaintext over
    /// a fresh connection) - but once a plaintext handshake connection is found, withholds its
    /// own handshake reply until <paramref name="releaseHandshake"/> is set.
    /// </summary>
    private static async Task RunHandshakeStallingSeederAsync(TcpListener listener, ManualResetEventSlim releaseHandshake)
    {
        TcpClient client;
        NetworkStream stream;
        var incomingHandshake = new byte[68];

        while (true)
        {
            var candidate = await listener.AcceptTcpClientAsync();
            var candidateStream = candidate.GetStream();

            if (!await TryReadExactAsync(candidateStream, incomingHandshake.AsMemory(0, 1)))
            {
                candidate.Dispose();
                continue;
            }

            if (incomingHandshake[0] != 19)
            {
                candidate.Dispose();
                continue;
            }

            await ReadExactAsync(candidateStream, incomingHandshake.AsMemory(1, 67));
            client = candidate;
            stream = candidateStream;
            break;
        }

        using var __ = client;
        using var _ = stream;

        await Task.Run(() => releaseHandshake.Wait(TimeSpan.FromSeconds(10)));

        var infoHash = new byte[20];
        Array.Copy(incomingHandshake, 28, infoHash, 0, 20);
        await stream.WriteAsync(BuildHandshake(infoHash, "-bz0001-seeder000000"));
        await stream.WriteAsync(BuildMessage(5, new byte[] { 0x80 })); // bitfield: piece 0 set

        // Keep the connection open past the handshake so the leecher doesn't just see a
        // dropped connection right after completing it.
        await Task.Delay(2000);
    }

    private static byte[] BuildHandshake(byte[] infoHash, string peerId)
    {
        var buffer = new byte[68];
        buffer[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(buffer, 1);
        Array.Copy(infoHash, 0, buffer, 28, 20);
        Encoding.ASCII.GetBytes(peerId).CopyTo(buffer, 48);
        return buffer;
    }

    private static byte[] BuildMessage(byte messageId, byte[] payload)
    {
        var buffer = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(1 + payload.Length));
        buffer[4] = messageId;
        payload.CopyTo(buffer, 5);
        return buffer;
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        if (!await TryReadExactAsync(stream, buffer))
            throw new IOException("Connection closed before the expected bytes arrived.");
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..]);
            if (read == 0)
                return false;
            totalRead += read;
        }

        return true;
    }

    private static async Task<bool> SpinUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }
}
