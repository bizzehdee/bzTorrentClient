using Avalonia.Data.Converters;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

/// <summary>Dims a peer row's download/upload indicator when that direction isn't currently active.</summary>
public static class PeerActivityConverters
{
    public static readonly IValueConverter ToOpacity = new FuncValueConverter<bool, double>(isActive => isActive ? 1.0 : 0.35);

    public static readonly IValueConverter TransportToText = new FuncValueConverter<PeerTransportKind, string>(transport => transport switch
    {
        PeerTransportKind.Utp => "uTP",
        _ => "TCP",
    });
}
