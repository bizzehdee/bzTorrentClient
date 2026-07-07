namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

/// <summary>
/// Null <paramref name="Seeders"/>/<paramref name="Leechers"/>/<paramref name="LastAnnounceUtc"/>
/// mean this tracker hasn't answered at least once yet, as distinct from a confirmed 0.
/// </summary>
public sealed record TrackerRowViewModel(string Url, int PeersFound, int? Seeders, int? Leechers, DateTime? LastAnnounceUtc, string? LastError)
{
    public string SeedersText => Seeders?.ToString() ?? "—";
    public string LeechersText => Leechers?.ToString() ?? "—";

    public string LastAnnounceText => LastAnnounceUtc is { } lastAnnounce
        ? lastAnnounce.ToLocalTime().ToString("g")
        : LastError ?? "Not yet announced";
}
