using System.Collections.Concurrent;
using System.Net;
using bzBencode;
using bzTorrent;
using bzTorrent.Data;
using bzTorrent.IO;
using bzTorrent.ProtocolExtensions;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Best-effort BEP-9 metadata fetch for a magnet/info-hash-only add: pulls candidate
/// peers from an already-running <see cref="IPeerSource"/> and tries many of them at once
/// ("scattergun" - metadata is small and cheap to ask for, and any single candidate might
/// not have it, be slow, or not support the extension at all) until one hands over the
/// full info dictionary, or <paramref name="timeout"/> elapses.
/// </summary>
public static class MetadataFetcher
{
    // High enough that a magnet with a healthy swarm gets metadata within one or two
    // tracker/DHT announce cycles rather than working through a handful of candidates at a
    // time - metadata fetch attempts are cheap (one handshake + a few small extension
    // messages each), so erring toward "try lots of peers" costs little.
    private const int WorkerCount = 10;
    private const int PerPeerTimeoutSeconds = 15;

    public static async Task<bool> TryFetchAsync(
        Metadata metadata,
        IPeerSource peerSource,
        string localPeerId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(peerSource);

        if (metadata.PieceHashes.Count > 0)
            return true;

        var candidates = new ConcurrentQueue<IPEndPoint>();
        using var peerAvailable = new SemaphoreSlim(0);

        void OnPeerFound(IPEndPoint endpoint)
        {
            candidates.Enqueue(endpoint);
            peerAvailable.Release();
        }

        peerSource.PeerFound += OnPeerFound;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = timeoutCts.Token.Register(() => completion.TrySetResult(false)).ConfigureAwait(false);

            var workers = Enumerable.Range(0, WorkerCount)
                .Select(_ => Task.Run(
                    () => WorkerLoop(metadata, candidates, peerAvailable, localPeerId, completion, timeoutCts.Token),
                    CancellationToken.None))
                .ToArray();

            var succeeded = await completion.Task.ConfigureAwait(false);
            timeoutCts.Cancel();
            await Task.WhenAll(workers).ConfigureAwait(false);
            return succeeded;
        }
        finally
        {
            peerSource.PeerFound -= OnPeerFound;
        }
    }

    private static void WorkerLoop(
        Metadata metadata,
        ConcurrentQueue<IPEndPoint> candidates,
        SemaphoreSlim peerAvailable,
        string localPeerId,
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !completion.Task.IsCompleted)
        {
            if (!candidates.TryDequeue(out var endpoint))
            {
                try
                {
                    peerAvailable.Wait(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (TryFetchFromPeer(metadata, endpoint, localPeerId, cancellationToken))
            {
                completion.TrySetResult(true);
                return;
            }
        }
    }

    private static bool TryFetchFromPeer(Metadata metadata, IPEndPoint endpoint, string localPeerId, CancellationToken cancellationToken)
    {
        var (connected, fetched) = TryFetchFromPeerOverTransport<TCPSocket>(metadata, endpoint, localPeerId, cancellationToken);
        if (connected)
            return fetched;

        // Some peers are only reachable over uTP (BEP-29) - e.g. behind a NAT/firewall
        // that passes outbound UDP but blocks an unsolicited inbound TCP SYN. Only worth
        // a retry when TCP never connected at all; a peer that connected but didn't have
        // (or wouldn't share) the metadata isn't going to behave differently over uTP.
        (_, fetched) = TryFetchFromPeerOverTransport<UTPConnection>(metadata, endpoint, localPeerId, cancellationToken);
        return fetched;
    }

    /// <returns>
    /// (connected, fetched) - connected is false only when the connection itself never
    /// came up (the case worth retrying over a different transport); fetched is only
    /// meaningful when connected is true.
    /// </returns>
    private static (bool connected, bool fetched) TryFetchFromPeerOverTransport<TSocket>(
        Metadata metadata, IPEndPoint endpoint, string localPeerId, CancellationToken cancellationToken)
        where TSocket : ISocket, new()
    {
        // See PeerConnectionManager's matching comment: PlainText-only meant peers requiring
        // MSE/PE encryption (a large share of the real swarm) never completed a handshake,
        // so metadata never arrived even though tracker/DHT found plenty of peers.
        var connection = new PeerWireConnection<TSocket> { Timeout = PerPeerTimeoutSeconds, EncryptionMode = PeerEncryptionMode.PreferEncryption };
        var client = new PeerWireClient(connection);

        var utMetadata = new UTMetadata();
        var received = new TaskCompletionSource<BDict>(TaskCreationOptions.RunContinuationsAsynchronously);
        utMetadata.MetaDataReceived += (_, _, dict) => received.TrySetResult(dict);

        var extendedProtocol = new ExtendedProtocolExtensions();
        extendedProtocol.RegisterProtocolExtension(client, utMetadata);
        client.RegisterBTExtension(extendedProtocol);

        try
        {
            client.Connect(endpoint);
        }
        catch (Exception)
        {
            return (false, false);
        }

        try
        {
            client.Handshake(metadata.HashString, localPeerId);

            var deadline = DateTime.UtcNow.AddSeconds(PerPeerTimeoutSeconds);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested && !received.Task.IsCompleted && client.Process())
            {
                Thread.Sleep(10);
            }

            return (true, received.Task.IsCompletedSuccessfully && LoadFetchedMetadata(metadata, received.Task.Result));
        }
        catch (Exception)
        {
            // Any failure here just means this one candidate peer didn't pan out — the
            // caller moves on to the next candidate, so this must never fault the worker.
            return (true, false);
        }
        finally
        {
            PeerWireSafety.SafeDisconnect(client);
        }
    }

    /// <summary>
    /// <see cref="Metadata.LoadInfoDictionary"/> alone populates piece hashes/files/name
    /// etc. but never sets the metadata's internal bencoded root document - so a later
    /// <see cref="Metadata.Save"/> (needed to cache the fetched metadata to disk against
    /// the torrent's info-hash) throws "No metadata to save", silently losing the fetch.
    /// Wrapping the received info dict in a minimal root and going through the public
    /// <see cref="Metadata.Load(Stream)"/> instead sets that root too, so Save works from
    /// here on - same effect as loading a real .torrent file's bytes.
    /// </summary>
    internal static bool LoadFetchedMetadata(Metadata metadata, BDict infoDict)
    {
        var root = new BDict { ["info"] = infoDict };

        using var stream = new MemoryStream();
        BencodingUtils.Encode(root, stream);
        stream.Position = 0;

        return metadata.Load(stream);
    }
}
