namespace bzTorrentClient.Engine.Networking;

internal static class AsyncUtil
{
    /// <summary>Awaits <paramref name="delay"/>, returning false instead of throwing if <paramref name="cancellationToken"/> fires first.</summary>
    public static async Task<bool> TryDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
