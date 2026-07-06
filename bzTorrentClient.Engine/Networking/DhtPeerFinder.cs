using System.Net;
using System.Net.Sockets;
using bzTorrent.DHT;

namespace bzTorrentClient.Engine.Networking;

public sealed class DhtPeerFinder : IDhtPeerFinder
{
    private static readonly (string Host, int Port)[] BootstrapHosts =
    {
        ("router.bittorrent.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
    };

    private readonly DHTClient _client;

    public event Action<IPEndPoint>? PeerFound;

    public int NodeCount => _client.NodeCount;

    public DhtPeerFinder(int port = 0)
    {
        _client = new DHTClient(port);
        _client.PeerFound += endpoint => PeerFound?.Invoke(endpoint);
        _client.Start();
        _ = BootstrapAsync();
    }

    public void StartSearch(byte[] infoHash) => _client.StartSearch(infoHash);

    public void Dispose() => _client.Dispose();

    private async Task BootstrapAsync()
    {
        var nodes = new List<IPEndPoint>();
        foreach (var (host, port) in BootstrapHosts)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is not null)
                    nodes.Add(new IPEndPoint(ipv4, port));
            }
            catch
            {
                // A single unresolvable bootstrap host shouldn't prevent trying the others.
            }
        }

        if (nodes.Count > 0)
            await _client.BootstrapAsync(nodes);
    }
}
