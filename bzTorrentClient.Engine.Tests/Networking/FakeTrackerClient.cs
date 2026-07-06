using bzTorrent;

namespace bzTorrentClient.Engine.Tests.Networking;

internal sealed class FakeTrackerClient : ITrackerClient
{
    private readonly Func<AnnounceRequest, AnnounceInfo?> _respond;

    public FakeTrackerClient(Func<AnnounceRequest, AnnounceInfo?> respond)
    {
        _respond = respond;
    }

    public string Tracker => string.Empty;
    public int Port => 0;

    public int AnnounceCallCount { get; private set; }

    public AnnounceInfo Announce(string url, string hash, string peerId) =>
        Announce(new AnnounceRequest { Url = url, Hash = hash, PeerId = peerId });

    public AnnounceInfo Announce(AnnounceRequest request)
    {
        AnnounceCallCount++;
        return _respond(request) ?? throw new InvalidOperationException("Simulated tracker failure.");
    }

    public IDictionary<string, AnnounceInfo> Announce(string url, string[] hashes, string peerId) =>
        new Dictionary<string, AnnounceInfo>();

    public IDictionary<string, ScrapeInfo> Scrape(string url, string[] hashes) =>
        new Dictionary<string, ScrapeInfo>();
}
