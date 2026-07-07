using System.Collections.Concurrent;
using System.Net;
using bzTorrent;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Tests.Testing;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

public class AggregatingPeerSourceTests
{
    [Fact]
    public async Task Start_AnnouncesToTrackersAndRaisesDiscoveredPeers()
    {
        var announced = new SemaphoreSlim(0);
        var trackerClient = new FakeTrackerClient(request =>
        {
            var info = new AnnounceInfo(new[] { new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6001) }, waitTime: 3600, seeders: 1, leechers: 0);
            announced.Release();
            return info;
        });

        var metadata = new FakeMetadata(1) { };
        metadata.AnnounceList.Add("http://tracker.example/announce");

        var found = new ConcurrentBag<IPEndPoint>();
        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => trackerClient,
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder());
        source.PeerFound += found.Add;

        source.Start();
        var signalled = await announced.WaitAsync(TimeSpan.FromSeconds(5));
        source.Stop();

        Assert.True(signalled);
        Assert.Contains(found, ep => ep.Address.ToString() == "10.0.0.1" && ep.Port == 6001);
    }

    [Fact]
    public void Start_NonPrivateTorrent_StartsDhtSearchAndLanAnnounce()
    {
        var dht = new FakePeerFinder();
        var lan = new FakePeerFinder();
        var metadata = new FakeMetadata(1, hashHex: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => dht,
            lanPeerFinderFactory: () => lan);

        source.Start();
        source.Stop();

        Assert.NotNull(dht.SearchedInfoHash);
        Assert.Equal((6881, metadata.HashString), lan.Announced);
    }

    [Fact]
    public void Start_PrivateTorrent_SkipsDhtAndLan()
    {
        var dhtFactoryCalled = false;
        var lanFactoryCalled = false;
        var metadata = new FakeMetadata(1) { Private = true };

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => { dhtFactoryCalled = true; return new FakePeerFinder(); },
            lanPeerFinderFactory: () => { lanFactoryCalled = true; return new FakePeerFinder(); });

        source.Start();
        source.Stop();

        Assert.False(dhtFactoryCalled);
        Assert.False(lanFactoryCalled);
    }

    [Fact]
    public void PeerFound_DeduplicatesSameEndpointAcrossSources()
    {
        var dht = new FakePeerFinder();
        var lan = new FakePeerFinder();
        var metadata = new FakeMetadata(1);

        var found = new List<IPEndPoint>();
        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => dht,
            lanPeerFinderFactory: () => lan);
        source.PeerFound += found.Add;

        source.Start();
        var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 6881);
        dht.Raise(endpoint);
        lan.Raise(endpoint);
        source.Stop();

        Assert.Single(found);
    }

    [Fact]
    public void PeerFound_TracksCountsPerDiscoverySource()
    {
        var dht = new FakePeerFinder { NodeCount = 12 };
        var lan = new FakePeerFinder();
        var metadata = new FakeMetadata(1);

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => dht,
            lanPeerFinderFactory: () => lan);

        source.Start();
        dht.Raise(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 6881));
        dht.Raise(new IPEndPoint(IPAddress.Parse("192.168.1.6"), 6881));
        lan.Raise(new IPEndPoint(IPAddress.Parse("192.168.1.7"), 6881));

        Assert.Equal(2, source.DhtPeersFound);
        Assert.Equal(1, source.LanPeersFound);
        Assert.Equal(0, source.TrackerPeersFound);
        Assert.Equal(12, source.DhtNodeCount);

        source.Stop();

        // Node count reflects a disposed/absent finder once stopped.
        Assert.Equal(0, source.DhtNodeCount);
    }

    [Fact]
    public async Task PeerFound_TracksTrackerPeerCount()
    {
        var announced = new SemaphoreSlim(0);
        var trackerClient = new FakeTrackerClient(request =>
        {
            var info = new AnnounceInfo(
                new[] { new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6001), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 6002) },
                waitTime: 3600, seeders: 2, leechers: 0);
            announced.Release();
            return info;
        });

        var metadata = new FakeMetadata(1);
        metadata.AnnounceList.Add("http://tracker.example/announce");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => trackerClient,
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder());

        source.Start();
        await announced.WaitAsync(TimeSpan.FromSeconds(5));
        source.Stop();

        Assert.Equal(2, source.TrackerPeersFound);
    }

    [Fact]
    public void Stop_DisposesDhtAndLanFinders()
    {
        var dht = new FakePeerFinder();
        var lan = new FakePeerFinder();
        var metadata = new FakeMetadata(1);

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => dht,
            lanPeerFinderFactory: () => lan);

        source.Start();
        source.Stop();

        Assert.True(dht.Disposed);
        Assert.True(lan.Disposed);
    }

    [Fact]
    public async Task PollTracker_FailsRepeatedly_RemovesTrackerFromAnnounceList()
    {
        var metadata = new FakeMetadata(1);
        metadata.AnnounceList.Add("http://dead-tracker.example/announce");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder(),
            trackerFailureRetryDelay: TimeSpan.FromMilliseconds(10));

        source.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (metadata.AnnounceList.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        source.Stop();

        Assert.Empty(metadata.AnnounceList);
    }

    [Fact]
    public async Task PollTracker_SucceedsAfterAFailure_KeepsTrackerAndResetsFailureCount()
    {
        var callCount = 0;
        var succeededTwice = new SemaphoreSlim(0);
        var trackerClient = new FakeTrackerClient(request =>
        {
            callCount++;
            if (callCount == 1)
                return null; // one failure, not enough to give up on the tracker

            var info = new AnnounceInfo(Array.Empty<IPEndPoint>(), waitTime: 3600, seeders: 0, leechers: 0);
            succeededTwice.Release();
            return info;
        });

        var metadata = new FakeMetadata(1);
        metadata.AnnounceList.Add("http://flaky-tracker.example/announce");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => trackerClient,
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder(),
            trackerFailureRetryDelay: TimeSpan.FromMilliseconds(10));

        source.Start();
        var signalled = await succeededTwice.WaitAsync(TimeSpan.FromSeconds(5));
        source.Stop();

        Assert.True(signalled);
        Assert.Contains("http://flaky-tracker.example/announce", metadata.AnnounceList);
    }

    [Fact]
    public async Task PollTracker_SuccessfulAnnounce_RecordsTrackerStatus()
    {
        var announced = new SemaphoreSlim(0);
        var trackerClient = new FakeTrackerClient(request =>
        {
            var info = new AnnounceInfo(
                new[] { new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6001), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 6002) },
                waitTime: 3600, seeders: 5, leechers: 2);
            announced.Release();
            return info;
        });

        var metadata = new FakeMetadata(1);
        metadata.AnnounceList.Add("http://tracker.example/announce");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => trackerClient,
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder());

        source.Start();
        await announced.WaitAsync(TimeSpan.FromSeconds(5));
        source.Stop();

        var status = Assert.Single(source.TrackerStatuses);
        Assert.Equal("http://tracker.example/announce", status.Url);
        Assert.Equal(2, status.PeersFound);
        Assert.Equal(5, status.Seeders);
        Assert.Equal(2, status.Leechers);
        Assert.NotNull(status.LastAnnounceUtc);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task PollTracker_FailingTracker_RecordsLastErrorUntilDropped()
    {
        var metadata = new FakeMetadata(1);
        metadata.AnnounceList.Add("http://dead-tracker.example/announce");

        var source = new AggregatingPeerSource(
            metadata,
            listenPort: 6881,
            localPeerId: "-bz0001-000000000000",
            trackerClientFactory: _ => new FakeTrackerClient(_ => null),
            dhtPeerFinderFactory: () => new FakePeerFinder(),
            lanPeerFinderFactory: () => new FakePeerFinder(),
            trackerFailureRetryDelay: TimeSpan.FromSeconds(5));

        source.Start();

        // Caught mid-flight, before the 3rd failure drops the tracker entirely (that
        // scenario is covered by PollTracker_FailsRepeatedly_RemovesTrackerFromAnnounceList).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        TrackerStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = source.TrackerStatuses.FirstOrDefault();
            if (status is not null)
                break;
            await Task.Delay(20);
        }

        source.Stop();

        Assert.NotNull(status);
        Assert.Null(status!.LastAnnounceUtc);
        Assert.NotNull(status.LastError);
    }
}
