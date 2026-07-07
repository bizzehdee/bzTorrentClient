using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Tests.Networking;

internal sealed class FakeDefaultTrackerListProvider : IDefaultTrackerListProvider
{
    private readonly IReadOnlyList<string> _trackers;

    public int RefreshCount { get; private set; }

    public FakeDefaultTrackerListProvider(params string[] trackers) => _trackers = trackers;

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetTrackers() => _trackers;
}
