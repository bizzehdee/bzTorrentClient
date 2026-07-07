using Avalonia.Data.Converters;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Avalonia.Features.Settings;

public static class ProtocolModeConverters
{
    public static readonly IValueConverter ToDisplayText = new FuncValueConverter<ProtocolMode, string>(mode => mode switch
    {
        ProtocolMode.TcpOnly => "TCP only",
        ProtocolMode.UtpOnly => "µTP only",
        _ => "TCP and µTP (prefer TCP)",
    });
}
