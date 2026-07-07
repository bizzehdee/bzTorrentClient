using System.Net;
using System.Text.Json;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// A single JSON file mapping info-hash → last-known peer endpoints. Loaded into memory once;
/// each <see cref="Save"/> updates memory and rewrites the file. All access is serialized, and
/// every failure is swallowed - a missing/corrupt cache just means starting cold, never a crash.
/// </summary>
public sealed class JsonPeerCacheStore : IPeerCacheStore
{
    // Plenty to re-seed a swarm from, but bounded so a long-lived torrent's cache can't grow
    // without limit; we keep the most recently connected peers.
    private const int MaxPeersPerTorrent = 200;

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _byInfoHash;

    public JsonPeerCacheStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be empty.", nameof(filePath));

        _filePath = filePath;
        _byInfoHash = LoadFile(filePath);
    }

    public IReadOnlyList<IPEndPoint> Load(string infoHash)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
            return Array.Empty<IPEndPoint>();

        lock (_gate)
        {
            if (!_byInfoHash.TryGetValue(Normalize(infoHash), out var raw))
                return Array.Empty<IPEndPoint>();

            var peers = new List<IPEndPoint>(raw.Count);
            foreach (var entry in raw)
            {
                if (IPEndPoint.TryParse(entry, out var endpoint))
                    peers.Add(endpoint);
            }

            return peers;
        }
    }

    public void Save(string infoHash, IReadOnlyCollection<IPEndPoint> peers)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
            return;

        ArgumentNullException.ThrowIfNull(peers);

        lock (_gate)
        {
            var key = Normalize(infoHash);

            if (peers.Count == 0)
            {
                // Nothing to remember - drop any stale entry rather than persisting emptiness.
                if (!_byInfoHash.Remove(key))
                    return;
            }
            else
            {
                _byInfoHash[key] = peers
                    .Take(MaxPeersPerTorrent)
                    .Select(p => p.ToString())
                    .ToList();
            }

            WriteFile();
        }
    }

    private void WriteFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_byInfoHash, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed peer-cache write must never disrupt the client.
        }
    }

    private static Dictionary<string, List<string>> LoadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(filePath));
            return loaded is null
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Normalize(string infoHash) => infoHash.Trim().ToLowerInvariant();
}
