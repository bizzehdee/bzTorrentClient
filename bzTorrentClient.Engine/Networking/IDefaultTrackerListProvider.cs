namespace bzTorrentClient.Engine.Networking;

/// <summary>
/// Supplies a combined, deduped list of trackers to upsert into every non-private
/// torrent's own tracker list, sourced from a user-configured URL (auto-refreshed) and a
/// user-configured text block — see <see cref="Settings.IClientSettings.DefaultTrackerListUrl"/>
/// and <see cref="Settings.IClientSettings.DefaultTrackerListText"/>.
/// </summary>
public interface IDefaultTrackerListProvider
{
    /// <summary>
    /// Best-effort re-fetch of the URL-sourced list. Safe to call when the URL is unset or
    /// unreachable — failures are swallowed and the previously cached list (if any) is kept.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Combined, deduped, validated trackers from both sources, in encounter order.</summary>
    IReadOnlyList<string> GetTrackers();
}
