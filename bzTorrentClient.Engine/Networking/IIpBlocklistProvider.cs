using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Combines a URL, a local file, and free-text settings into a single IP blocklist.</summary>
public interface IIpBlocklistProvider
{
    /// <summary>Re-fetches the URL source and re-reads the local file/text sources, rebuilding the combined blocklist.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a peer at this address must not be connected to, downloaded from, or uploaded to.</summary>
    bool IsBlocked(IPAddress address);
}
