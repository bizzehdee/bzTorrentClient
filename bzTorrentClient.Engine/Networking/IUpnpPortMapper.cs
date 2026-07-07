namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Best-effort router port forwarding for the listen port. <see cref="Start"/> begins
/// discovering NAT gateways and forwarding the given port (both TCP and UDP); <see cref="Stop"/>
/// removes the mappings. All operations are best-effort - a router without UPnP/NAT-PMP, or
/// with it disabled, simply leaves the port unforwarded.
/// </summary>
public interface IUpnpPortMapper : IDisposable
{
    void Start(int port);
    void Stop();
}
