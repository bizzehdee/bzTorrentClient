namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Classic token bucket: tokens (bytes) refill continuously at the configured rate, up to a
/// one-second burst cap. The limit is read from <paramref name="bytesPerSecondProvider"/> on
/// every call rather than captured once, so changing the setting at runtime (the Settings
/// dialog) takes effect immediately without needing to reconstruct or notify this instance.
/// A limit of zero or less means unlimited.
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly Func<long> _bytesPerSecondProvider;
    private readonly object _lock = new();

    // Starts uninitialized rather than at 0: the bucket fills to a full second's worth the
    // first time it's actually used (see Refill), so a freshly (re)started limiter can serve
    // an immediate burst instead of stalling the very first request while nothing has been
    // transferred yet.
    private double? _tokens;
    private DateTime _lastRefillUtc;

    public TokenBucketRateLimiter(Func<long> bytesPerSecondProvider)
    {
        _bytesPerSecondProvider = bytesPerSecondProvider ?? throw new ArgumentNullException(nameof(bytesPerSecondProvider));
    }

    public bool TryConsume(int bytes)
    {
        if (bytes <= 0)
            return true;

        lock (_lock)
        {
            var limit = _bytesPerSecondProvider();
            if (limit <= 0)
                return true;

            Refill(limit);

            if (_tokens < bytes)
                return false;

            _tokens -= bytes;
            return true;
        }
    }

    private void Refill(long limit)
    {
        var now = DateTime.UtcNow;

        if (_tokens is null)
        {
            _tokens = limit;
            _lastRefillUtc = now;
            return;
        }

        var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
        _lastRefillUtc = now;

        if (elapsedSeconds <= 0)
            return;

        // Cap the bucket at one second's worth so a long idle period doesn't let a huge
        // burst through immediately once traffic resumes.
        _tokens = Math.Min(limit, _tokens.Value + elapsedSeconds * limit);
    }
}
