namespace bzTorrentClient.Engine.Networking;

/// <summary>A shared, global throughput cap. One instance is used across every torrent's connection manager so the limit applies to the whole client, not per torrent.</summary>
public interface IRateLimiter
{
    /// <summary>Attempts to spend <paramref name="bytes"/> from the current budget. Returns false (and consumes nothing) if that would exceed the configured rate.</summary>
    bool TryConsume(int bytes);
}
