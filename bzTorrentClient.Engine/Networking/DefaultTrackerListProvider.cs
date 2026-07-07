using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Engine.Networking;

public sealed class DefaultTrackerListProvider : IDefaultTrackerListProvider
{
    private static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(15);

    private readonly IClientSettings _settings;
    private readonly string _cacheFilePath;
    private readonly Func<string, CancellationToken, Task<string>> _fetcher;
    private readonly object _lock = new();
    private string _cachedUrlListText = string.Empty;

    public DefaultTrackerListProvider(
        IClientSettings settings,
        string cacheFilePath,
        Func<string, CancellationToken, Task<string>>? fetcher = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (string.IsNullOrWhiteSpace(cacheFilePath))
            throw new ArgumentException("Cache file path must not be empty.", nameof(cacheFilePath));

        _cacheFilePath = cacheFilePath;
        _fetcher = fetcher ?? DefaultFetchAsync;

        try
        {
            if (File.Exists(_cacheFilePath))
                _cachedUrlListText = File.ReadAllText(_cacheFilePath);
        }
        catch (IOException)
        {
            // Best-effort - an unreadable cache file just means we start empty until the
            // next successful refresh.
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var url = _settings.DefaultTrackerListUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var text = await _fetcher(url, cancellationToken);

            lock (_lock)
                _cachedUrlListText = text;

            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_cacheFilePath, text);
            }
            catch (IOException)
            {
                // Best-effort - the in-memory copy from this refresh is still usable even
                // if it can't be cached to disk for next launch.
            }
        }
        catch (Exception)
        {
            // Best-effort: a network hiccup at launch must not wipe out a previously
            // cached list, nor stop the app from starting.
        }
    }

    public IReadOnlyList<string> GetTrackers()
    {
        string urlListText;
        lock (_lock)
            urlListText = _cachedUrlListText;

        var trackers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in EnumerateLines(urlListText).Concat(EnumerateLines(_settings.DefaultTrackerListText)))
        {
            if (!TryNormalizeTrackerUrl(line, out var normalized))
                continue;

            if (seen.Add(normalized))
                trackers.Add(normalized);
        }

        return trackers;
    }

    private static IEnumerable<string> EnumerateLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Enumerable.Empty<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Lines that aren't a well-formed http(s)/udp tracker announce URL are silently ignored.</summary>
    private static bool TryNormalizeTrackerUrl(string line, out string normalized)
    {
        normalized = line.TrimEnd('/');

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme is "http" or "https" or "udp";
    }

    private static async Task<string> DefaultFetchAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = DefaultFetchTimeout };
        return await http.GetStringAsync(url, cancellationToken);
    }
}
