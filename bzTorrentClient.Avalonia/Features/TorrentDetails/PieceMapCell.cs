namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

/// <summary>One cell of the piece-map heat strip: the verified fraction (0..1) of the piece range it represents.</summary>
public sealed record PieceMapCell(double Fill);
