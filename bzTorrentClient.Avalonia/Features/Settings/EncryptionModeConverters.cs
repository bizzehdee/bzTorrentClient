using Avalonia.Data.Converters;
using bzTorrent.IO;

namespace bzTorrentClient.Avalonia.Features.Settings;

public static class EncryptionModeConverters
{
    public static readonly IValueConverter ToDisplayText = new FuncValueConverter<PeerEncryptionMode, string>(mode => mode switch
    {
        PeerEncryptionMode.PlainText => "Disabled",
        PeerEncryptionMode.RequireEncryption => "Require",
        _ => "Allow (preferred)",
    });
}
