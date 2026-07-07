using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>A DHT routing-table node (its id and contact endpoint) - the unit persisted between runs so the DHT can start warm instead of cold-bootstrapping every launch.</summary>
public sealed record DhtNodeInfo(byte[] Id, IPEndPoint EndPoint);
