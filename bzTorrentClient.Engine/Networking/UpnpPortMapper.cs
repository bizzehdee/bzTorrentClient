using Mono.Nat;
using bzTorrentClient.Engine.Logging;

namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Forwards the listen port on the local router via UPnP-IGD or NAT-PMP/PCP, using Mono.Nat.
/// Maps <b>both TCP and UDP</b> for the port - TCP for classic peer connections and UDP for
/// uTP (BEP-29) - so an inbound peer can reach us over either transport, and removes the
/// mappings again on <see cref="Stop"/>.
///
/// Everything here is best-effort: routers vary wildly, many ignore requested lease times or
/// have UPnP disabled entirely, so every gateway interaction is wrapped and failures are
/// logged rather than thrown - a torrent client must still work behind a router that won't
/// forward anything. <see cref="NatUtility"/> is process-global, so a single instance owns it.
/// </summary>
public sealed class UpnpPortMapper : IUpnpPortMapper
{
    // Requested lease length. Renewed on a timer at half this, because plenty of routers cap
    // or ignore long/"permanent" (0) leases and silently drop the mapping after a few minutes.
    private static readonly TimeSpan MappingLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(15);
    private const string MappingDescription = "bzTorrent Client";

    private static readonly Protocol[] Protocols = { Protocol.Tcp, Protocol.Udp };

    private readonly IDebugLogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<INatDevice> _devices = new();
    private Timer? _renewTimer;
    private int _port;
    private bool _running;

    public UpnpPortMapper(IDebugLogger? logger = null)
    {
        _logger = logger ?? NullDebugLogger.Instance;
    }

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_running)
                return;

            _running = true;
            _port = port;
        }

        NatUtility.DeviceFound += OnDeviceFound;

        try
        {
            NatUtility.StartDiscovery();
            _logger.Log($"UPnP: discovering gateways to forward port {port} (TCP+UDP).");
        }
        catch (Exception ex)
        {
            _logger.Log($"UPnP: failed to start discovery: {ex.Message}");
        }

        // Re-assert the mappings periodically so a router that quietly expired the lease
        // re-learns it without waiting for the next app restart.
        _renewTimer = new Timer(_ => RenewAll(), null, RenewInterval, RenewInterval);
    }

    public void Stop()
    {
        INatDevice[] devices;
        int port;
        lock (_gate)
        {
            if (!_running)
                return;

            _running = false;
            devices = _devices.ToArray();
            _devices.Clear();
            port = _port;
        }

        NatUtility.DeviceFound -= OnDeviceFound;
        _renewTimer?.Dispose();
        _renewTimer = null;

        try
        {
            NatUtility.StopDiscovery();
        }
        catch (Exception ex)
        {
            _logger.Log($"UPnP: failed to stop discovery: {ex.Message}");
        }

        // Called from the app's synchronous shutdown handler, so block briefly on each delete
        // rather than fire-and-forget (the process may exit before an un-awaited task runs).
        // A delete that doesn't land in time is harmless - the lease expires on its own.
        foreach (var device in devices)
        {
            foreach (var protocol in Protocols)
            {
                try
                {
                    device.DeletePortMapAsync(new Mapping(protocol, port, port))
                        .Wait(TimeSpan.FromSeconds(2));
                    _logger.Log($"UPnP: removed {protocol} mapping for port {port}.");
                }
                catch (Exception ex)
                {
                    _logger.Log($"UPnP: failed to remove {protocol} mapping for port {port}: {Unwrap(ex)}");
                }
            }
        }
    }

    public void Dispose() => Stop();

    private async void OnDeviceFound(object? sender, DeviceEventArgs e)
    {
        // async void event handler: it must never let an exception escape (that would crash
        // the process), so the whole body is guarded.
        try
        {
            int port;
            lock (_gate)
            {
                if (!_running)
                    return;

                port = _port;
                if (!_devices.Add(e.Device))
                    return; // already mapped on this device
            }

            _logger.Log($"UPnP: found gateway {Describe(e.Device)}; forwarding port {port}.");
            await MapAsync(e.Device, port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log($"UPnP: error handling discovered gateway: {Unwrap(ex)}");
        }
    }

    private async Task MapAsync(INatDevice device, int port)
    {
        foreach (var protocol in Protocols)
        {
            try
            {
                await device.CreatePortMapAsync(
                    new Mapping(protocol, port, port, (int)MappingLifetime.TotalSeconds, MappingDescription))
                    .ConfigureAwait(false);
                _logger.Log($"UPnP: forwarded {protocol} port {port} on {Describe(device)}.");
            }
            catch (Exception ex)
            {
                _logger.Log($"UPnP: failed to forward {protocol} port {port}: {Unwrap(ex)}");
            }
        }
    }

    private void RenewAll()
    {
        INatDevice[] devices;
        int port;
        lock (_gate)
        {
            if (!_running)
                return;

            devices = _devices.ToArray();
            port = _port;
        }

        foreach (var device in devices)
            _ = MapAsync(device, port);
    }

    private static string Describe(INatDevice device)
    {
        try
        {
            return device.DeviceEndpoint?.ToString() ?? "gateway";
        }
        catch
        {
            return "gateway";
        }
    }

    private static string Unwrap(Exception ex) =>
        ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
}
