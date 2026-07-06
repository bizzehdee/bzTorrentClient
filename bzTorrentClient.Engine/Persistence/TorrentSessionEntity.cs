using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Persistence;

public sealed class TorrentSessionEntity
{
    public Guid Id { get; set; }
    public string InfoHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public byte[]? TorrentFileBytes { get; set; }
    public string? MagnetUri { get; set; }
    public string DownloadDirectory { get; set; } = string.Empty;
    public TorrentState State { get; set; }
    public byte[] PieceCompletion { get; set; } = Array.Empty<byte>();
    public int PieceCount { get; set; }
    public DateTime AddedAtUtc { get; set; }
}
