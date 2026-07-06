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
/// peers from an already-running <see cref="IPeerSource"/> and tries them (a few in
/// parallel) until one hands over the full info dictionary, or <paramref name="timeout"/>
/// elapses.
/// </summary>
public static class MetadataFetcher
{
    private const int WorkerCount = 4;
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
        var connection = new PeerWireConnection<TCPSocket> { Timeout = PerPeerTimeoutSeconds, EncryptionMode = PeerEncryptionMode.PlainText };
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
            client.Handshake(metadata.HashString, localPeerId);

            var deadline = DateTime.UtcNow.AddSeconds(PerPeerTimeoutSeconds);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested && !received.Task.IsCompleted && client.Process())
            {
                Thread.Sleep(10);
            }

            return received.Task.IsCompletedSuccessfully && metadata.LoadInfoDictionary(received.Task.Result);
        }
        catch (Exception)
        {
            // Any failure here just means this one candidate peer didn't pan out — the
            // caller moves on to the next candidate, so this must never fault the worker.
            return false;
        }
        finally
        {
            PeerWireSafety.SafeDisconnect(client);
        }
    }
}
