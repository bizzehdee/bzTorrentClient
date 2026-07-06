namespace bzTorrentClient.Avalonia;

/// <summary>Human-readable byte/rate formatting shared across the torrent list, details, and footer.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Bumping a unit early leaves a barely-there fraction ("1.2 KB" for 1200 bytes); waiting too long leaves an unreadable pile of digits. Stepping up only once the value would read as ten or more of the next unit keeps every displayed number in a comfortable, glanceable range.</summary>
    private const double StepUpThreshold = 1024 * 10;

    public static string Bytes(double bytes)
    {
        if (bytes < 0)
            bytes = 0;

        var unit = 0;
        while (unit < Units.Length - 1 && bytes >= StepUpThreshold)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{bytes:0.#} {Units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";
}
