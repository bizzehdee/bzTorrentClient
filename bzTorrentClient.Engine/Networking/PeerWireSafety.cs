using System.IO;
using System.Net.Sockets;
using bzTorrent;

namespace bzTorrentClient.Engine.Networking;

internal static class PeerWireSafety
{
    /// <summary>
    /// bzTorrent's <see cref="IPeerWireClient.Disconnect"/> isn't idempotent — it NREs (or
    /// throws a raw <see cref="SocketException"/>, e.g. ENOTCONN) if the underlying
    /// connection was already torn down, which happens routinely: a peer's own Process()
    /// loop can detect a dropped connection and disconnect internally before our code's
    /// own cleanup path calls Disconnect() again. Every Disconnect() call on a client we
    /// don't fully control the lifecycle of must go through this instead of a bare call.
    /// </summary>
    public static void SafeDisconnect(IPeerWireClient client)
    {
        try
        {
            client.Disconnect();
        }
        catch (Exception ex) when (ex is NullReferenceException or SocketException or IOException)
        {
        }
    }
}
