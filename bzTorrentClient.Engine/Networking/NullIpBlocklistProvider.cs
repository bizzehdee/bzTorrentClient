using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Never blocks anyone - used wherever a blocklist provider is optional and none was configured.</summary>
public sealed class NullIpBlocklistProvider : IIpBlocklistProvider
{
    public static readonly NullIpBlocklistProvider Instance = new();

    private NullIpBlocklistProvider()
    {
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public bool IsBlocked(IPAddress address) => false;
}
