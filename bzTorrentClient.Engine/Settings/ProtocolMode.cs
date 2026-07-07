namespace bzTorrentClient.Engine.Settings;

/// <summary>
/// Which transport(s) the client uses for outbound peer connections.
/// </summary>
public enum ProtocolMode
{
    /// <summary>Only connect over TCP; never attempt uTP.</summary>
    TcpOnly,

    /// <summary>Try TCP first and fall back to uTP (BEP-29) only if the TCP connection can't be established. The default.</summary>
    TcpAndUtp,

    /// <summary>Only connect over uTP (BEP-29); never attempt TCP.</summary>
    UtpOnly,
}
