using Avalonia.Data.Converters;
using Avalonia.Media;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Features.TorrentList;

/// <summary>Maps a <see cref="TorrentState"/> to its status-dot color. Colors mirror the state brushes in Styles/Tokens.axaml (kept constant across light/dark, so duplicated here rather than resolved from Application.Resources).</summary>
public static class TorrentStateConverters
{
    private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#0EA5B7"));
    private static readonly IBrush Completed = new SolidColorBrush(Color.Parse("#4CAF7D"));
    private static readonly IBrush Seeding = new SolidColorBrush(Color.Parse("#2E8B57"));
    private static readonly IBrush Paused = new SolidColorBrush(Color.Parse("#E0A840"));
    private static readonly IBrush Stopped = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#D9534F"));

    public static readonly IValueConverter ToBrush = new FuncValueConverter<TorrentState, IBrush>(state => state switch
    {
        TorrentState.Active => Active,
        TorrentState.Completed => Completed,
        TorrentState.Seeding => Seeding,
        TorrentState.Paused => Paused,
        TorrentState.Checking => Paused,
        TorrentState.Stopped => Stopped,
        TorrentState.Error => Error,
        _ => Stopped,
    });
}
