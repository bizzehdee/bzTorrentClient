using bzTorrentClient.Engine.Networking;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Networking;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public void TryConsume_LimitIsZero_AlwaysAllowed()
    {
        var limiter = new TokenBucketRateLimiter(() => 0);

        Assert.True(limiter.TryConsume(1_000_000));
        Assert.True(limiter.TryConsume(1_000_000));
    }

    [Fact]
    public void TryConsume_NonPositiveByteCount_AlwaysAllowed()
    {
        var limiter = new TokenBucketRateLimiter(() => 100);

        Assert.True(limiter.TryConsume(0));
        Assert.True(limiter.TryConsume(-5));
    }

    [Fact]
    public void TryConsume_WithinBudget_Succeeds()
    {
        var limiter = new TokenBucketRateLimiter(() => 1000);

        Assert.True(limiter.TryConsume(500));
    }

    [Fact]
    public void TryConsume_ExceedsBudget_Fails()
    {
        var limiter = new TokenBucketRateLimiter(() => 1000);

        Assert.True(limiter.TryConsume(1000));
        // Bucket should now be empty (or very nearly so) — immediately trying to spend
        // another full second's worth must fail.
        Assert.False(limiter.TryConsume(1000));
    }

    [Fact]
    public async Task TryConsume_RefillsOverTime()
    {
        var limiter = new TokenBucketRateLimiter(() => 1000);

        Assert.True(limiter.TryConsume(1000));
        Assert.False(limiter.TryConsume(1000));

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // At least half a second has passed at a 1000 bytes/sec rate, so at least ~500
        // bytes should have refilled.
        Assert.True(limiter.TryConsume(400));
    }

    [Fact]
    public void TryConsume_LimitChangesLive_ReflectsNewValueImmediately()
    {
        // The limiter reads the provider on every call rather than capturing it once —
        // this is what lets a live Settings-dialog change take effect without rebuilding
        // any PeerConnectionManager.
        var limit = 0L;
        var limiter = new TokenBucketRateLimiter(() => limit);

        Assert.True(limiter.TryConsume(1_000_000)); // unlimited while limit == 0

        limit = 100;
        Assert.True(limiter.TryConsume(100));
        Assert.False(limiter.TryConsume(100));
    }
}
