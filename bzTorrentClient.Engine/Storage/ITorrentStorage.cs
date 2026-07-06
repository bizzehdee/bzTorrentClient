namespace bzTorrentClient.Engine.Storage;

public interface ITorrentStorage
{
    /// <summary>Creates (and sizes) every file the torrent needs, if not already present.</summary>
    void EnsureAllocated();

    long GetPieceLength(int pieceIndex);

    void WriteBlock(int pieceIndex, int blockOffset, byte[] data);

    byte[] ReadPiece(int pieceIndex);
}
