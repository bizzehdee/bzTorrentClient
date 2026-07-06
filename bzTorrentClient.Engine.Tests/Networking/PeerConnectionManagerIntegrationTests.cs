using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using bzTorrent.Data;
using bzTorrentClient.Engine.Networking;
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

    [Fact]
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
        Assert.Contains(connectionManager.ConnectedEndpoints, ep => ep.Port == port);
        Assert.Equal(pieceData.Length, connectionManager.BytesDownloaded);

        connectionManager.Stop();
        tcpListener.Stop();

        Assert.True(completed, "Expected the single piece to be downloaded and verified within the timeout.");
        var downloaded = File.ReadAllBytes(Path.Combine(_leecherDir, "payload.bin"));
        Assert.Equal(pieceData, downloaded);
    }

    [Fact]
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

    /// <summary>Minimal BEP-3 peer: handshake, send an all-ones bitfield, unchoke on Interested, serve one Request.</summary>
    private static Task RunRawSeederAsync(TcpListener listener, byte[] pieceData) =>
        RunRawSeederAsync(listener, pieceData, requestsToServe: 1);

    /// <summary>Minimal BEP-3 peer: handshake, send an all-ones bitfield, unchoke on Interested, serve up to <paramref name="requestsToServe"/> Requests.</summary>
    private static async Task RunRawSeederAsync(TcpListener listener, byte[] pieceData, int requestsToServe)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();

        var incomingHandshake = new byte[68];
        await ReadExactAsync(stream, incomingHandshake);

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

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        if (!await TryReadExactAsync(stream, buffer))
            throw new IOException("Connection closed before the expected bytes arrived.");
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
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
