using System.Net;

namespace bzTorrentClient.Engine.Networking;

/// <summary>Which wire transport a peer connection is using.</summary>
public enum PeerTransportKind
{
    Tcp,
    Utp,
}

/// <summary>
/// A connected peer's cumulative transfer counters, plus which transport/encryption it's
/// using. Byte counters are deliberately raw totals rather than current rates/direction -
/// a UI samples the delta between refreshes to derive those, the same pattern already used
/// for torrent-level speeds.
/// </summary>
public sealed record PeerConnectionInfo(
    IPEndPoint EndPoint,
    long BytesDownloaded,
    long BytesUploaded,
    PeerTransportKind Transport,
    bool IsEncrypted);
