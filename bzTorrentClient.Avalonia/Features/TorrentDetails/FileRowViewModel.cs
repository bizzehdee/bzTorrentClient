namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

/// <summary>
/// <paramref name="ProgressPercent"/> is derived at piece granularity - a piece counts
/// toward every file it overlaps only once fully verified - the same approximation real
/// torrent clients use, since completion isn't tracked at sub-piece resolution.
/// </summary>
public sealed record FileRowViewModel(string Filename, long FileSize, double ProgressPercent);
