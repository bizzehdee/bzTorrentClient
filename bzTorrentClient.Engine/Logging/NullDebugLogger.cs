namespace bzTorrentClient.Engine.Logging;

/// <summary>Discards everything - used wherever a logger is optional and none was configured.</summary>
public sealed class NullDebugLogger : IDebugLogger
{
    public static readonly NullDebugLogger Instance = new();

    private NullDebugLogger()
    {
    }

    public void Log(string message)
    {
    }
}
