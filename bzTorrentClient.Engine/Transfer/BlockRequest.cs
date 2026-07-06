namespace bzTorrentClient.Engine.Transfer;

public sealed record BlockRequest(int PieceIndex, int BlockOffset, int Length);
