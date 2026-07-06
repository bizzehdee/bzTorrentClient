namespace bzTorrentClient.Avalonia;

/// <summary>Human-readable byte/rate formatting shared across the torrent list, details, and footer.</summary>
internal static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double bytes)
    {
        if (bytes < 0)
            bytes = 0;

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{bytes:0.#} {Units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";
}
