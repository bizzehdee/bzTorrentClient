using System.Net;
using System.Text.Json;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// Persists DHT nodes to a single JSON file as {id-hex, ip, port} entries. Best-effort: a
/// missing/corrupt file just means a cold start, and write failures are swallowed. Bounded so
/// the file can't grow without limit.
/// </summary>
public sealed class JsonDhtNodeStore : IDhtNodeStore
{
    private const int MaxNodes = 400;

    private readonly string _filePath;

    public JsonDhtNodeStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be empty.", nameof(filePath));

        _filePath = filePath;
    }

    public IReadOnlyList<DhtNodeInfo> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<DhtNodeInfo>();

            var dtos = JsonSerializer.Deserialize<List<NodeDto>>(File.ReadAllText(_filePath));
            if (dtos is null)
                return Array.Empty<DhtNodeInfo>();

            var nodes = new List<DhtNodeInfo>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (dto.Id is null || !IPAddress.TryParse(dto.Ip, out var address) || dto.Port is <= 0 or > 65535)
                    continue;

                byte[] id;
                try
                {
                    id = Convert.FromHexString(dto.Id);
                }
                catch (FormatException)
                {
                    continue;
                }

                nodes.Add(new DhtNodeInfo(id, new IPEndPoint(address, dto.Port)));
            }

            return nodes;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<DhtNodeInfo>();
        }
    }

    public void Save(IReadOnlyCollection<DhtNodeInfo> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var dtos = nodes
            .Take(MaxNodes)
            .Select(n => new NodeDto
            {
                Id = Convert.ToHexString(n.Id),
                Ip = n.EndPoint.Address.ToString(),
                Port = n.EndPoint.Port,
            })
            .ToList();

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed DHT-cache write must never disrupt the client.
        }
    }

    private sealed class NodeDto
    {
        public string? Id { get; set; }
        public string? Ip { get; set; }
        public int Port { get; set; }
    }
}
