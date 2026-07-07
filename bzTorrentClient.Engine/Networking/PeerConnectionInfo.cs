using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// A connected peer's cumulative transfer counters. Deliberately raw totals rather than
/// current rates/direction - a UI samples the delta between refreshes to derive those, the
/// same pattern already used for torrent-level speeds.
/// </summary>
public sealed record PeerConnectionInfo(IPEndPoint EndPoint, long BytesDownloaded, long BytesUploaded);
