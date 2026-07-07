namespace bzTorrentClient.Engine.Networking;

/// <summary>Used when no <see cref="IDefaultTrackerListProvider"/> is supplied - supplements nothing.</summary>
public sealed class NullDefaultTrackerListProvider : IDefaultTrackerListProvider
{
    public static readonly NullDefaultTrackerListProvider Instance = new();

    private NullDefaultTrackerListProvider()
    {
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<string> GetTrackers() => Array.Empty<string>();
}
