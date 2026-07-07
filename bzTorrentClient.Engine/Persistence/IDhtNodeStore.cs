using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// Persists the DHT routing table globally (not per-torrent - DHT nodes are info-hash
/// agnostic) so the DHT can start warm from known nodes rather than cold-bootstrapping on
/// every launch.
/// </summary>
public interface IDhtNodeStore
{
    IReadOnlyList<DhtNodeInfo> Load();

    void Save(IReadOnlyCollection<DhtNodeInfo> nodes);
}
