using System.Net;
using System.Net.Sockets;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Combines a URL (auto-updating, cached to disk between launches - same pattern as
/// <see cref="DefaultTrackerListProvider"/>), a local file, and free-text settings into one
/// IP blocklist. Accepts one entry per line in any of: a single IP ("1.2.3.4"), a CIDR range
/// ("1.2.3.0/24"), or an eMule/PeerGuardian-style range ("description:1.2.3.4-1.2.3.10",
/// the format Bluetack-style lists use). IPv4 only, matching the rest of this codebase.
/// </summary>
public sealed class IpBlocklistProvider : IIpBlocklistProvider
{
    private static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(15);

    private readonly IClientSettings _settings;
    private readonly string _cacheFilePath;
    private readonly Func<string, CancellationToken, Task<string>> _fetcher;
    private readonly object _lock = new();
    private string _cachedUrlListText = string.Empty;
    private (uint Start, uint End)[] _ranges = Array.Empty<(uint, uint)>();

    public IpBlocklistProvider(
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

        RebuildRanges();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var url = _settings.IpBlocklistUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
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
                    // Best-effort - the in-memory copy from this refresh is still usable
                    // even if it can't be cached to disk for next launch.
                }
            }
            catch (Exception)
            {
                // Best-effort: a network hiccup at launch must not wipe out a previously
                // cached list, nor stop the app from starting.
            }
        }

        RebuildRanges();
    }

    public bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var value = ToUInt32(address);

        (uint Start, uint End)[] ranges;
        lock (_lock)
            ranges = _ranges;

        // Ranges are sorted by Start and assumed non-overlapping (true of every blocklist
        // format this class parses) - the last range whose Start is <= value is the only
        // one that could possibly contain it.
        var lo = 0;
        var hi = ranges.Length - 1;
        var candidate = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (ranges[mid].Start <= value)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return candidate >= 0 && value <= ranges[candidate].End;
    }

    private void RebuildRanges()
    {
        string urlListText;
        lock (_lock)
            urlListText = _cachedUrlListText;

        var filePath = _settings.IpBlocklistFilePath;
        var fileText = string.Empty;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                if (File.Exists(filePath))
                    fileText = File.ReadAllText(filePath);
            }
            catch (IOException)
            {
                // Best-effort - an unreadable local blocklist file is skipped, not fatal.
            }
        }

        var ranges = new List<(uint Start, uint End)>();
        foreach (var line in EnumerateLines(urlListText).Concat(EnumerateLines(fileText)).Concat(EnumerateLines(_settings.IpBlocklistText)))
        {
            if (TryParseRange(line, out var range))
                ranges.Add(range);
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        lock (_lock)
            _ranges = ranges.ToArray();
    }

    private static IEnumerable<string> EnumerateLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Enumerable.Empty<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseRange(string rawLine, out (uint Start, uint End) range)
    {
        range = default;

        if (rawLine.Length == 0 || rawLine.StartsWith('#') || rawLine.StartsWith(';'))
            return false;

        // eMule/PeerGuardian-style "description:start_ip-end_ip" - the description may
        // itself contain ':' or '-', so only the text after the *last* ':' is tried as a
        // range; anything else falls through to being parsed as a bare line below.
        var colonIndex = rawLine.LastIndexOf(':');
        if (colonIndex >= 0 && TryParseIpDashIp(rawLine[(colonIndex + 1)..], out range))
            return true;

        if (TryParseIpDashIp(rawLine, out range))
            return true;

        if (TryParseCidr(rawLine, out range))
            return true;

        if (IPAddress.TryParse(rawLine, out var single) && single.AddressFamily == AddressFamily.InterNetwork)
        {
            var value = ToUInt32(single);
            range = (value, value);
            return true;
        }

        return false;
    }

    private static bool TryParseIpDashIp(string text, out (uint Start, uint End) range)
    {
        range = default;

        var dashIndex = text.IndexOf('-');
        if (dashIndex < 0)
            return false;

        if (!IPAddress.TryParse(text[..dashIndex].Trim(), out var start) || start.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (!IPAddress.TryParse(text[(dashIndex + 1)..].Trim(), out var end) || end.AddressFamily != AddressFamily.InterNetwork)
            return false;

        range = (ToUInt32(start), ToUInt32(end));
        return true;
    }

    private static bool TryParseCidr(string text, out (uint Start, uint End) range)
    {
        range = default;

        var slashIndex = text.IndexOf('/');
        if (slashIndex < 0)
            return false;

        if (!IPAddress.TryParse(text[..slashIndex], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (!int.TryParse(text[(slashIndex + 1)..], out var prefixLength) || prefixLength is < 0 or > 32)
            return false;

        var baseValue = ToUInt32(ip);
        var hostBits = 32 - prefixLength;
        var mask = hostBits == 32 ? 0u : ~((1u << hostBits) - 1);
        var start = baseValue & mask;
        var end = start | (hostBits == 32 ? 0xFFFFFFFFu : (1u << hostBits) - 1);
        range = (start, end);
        return true;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    private static async Task<string> DefaultFetchAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = DefaultFetchTimeout };
        return await http.GetStringAsync(url, cancellationToken);
    }
}
