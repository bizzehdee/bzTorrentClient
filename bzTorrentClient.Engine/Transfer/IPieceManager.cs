namespace bzTorrentClient.Engine.Transfer;

public interface IPieceManager
{
    bool IsComplete { get; }

    /// <summary>Raised once a piece finishes and verifies against its expected hash — this manager's own completion tracking is internal, so the session's progress/persistence state must be updated by whoever owns it, in response to this event.</summary>
    event Action<int>? PieceCompleted;

    bool IsPieceComplete(int pieceIndex);

    void RegisterPeerBitfield(int peerId, bool[] bitfield);
    void RegisterPeerHave(int peerId, int pieceIndex);
    void UnregisterPeer(int peerId);

    /// <summary>Picks the next block to request from a peer, preferring rarest pieces first and finishing in-progress pieces before starting new ones. Returns null if there's nothing this peer can currently supply.</summary>
    BlockRequest? TryGetNextRequest(int peerId, bool[] peerBitfield);

    /// <summary>
    /// Records a received block. Returns the piece index once that block completes a piece
    /// that verifies against its expected hash. A piece that fails verification is silently
    /// reset so its blocks get re-requested.
    /// </summary>
    int? OnBlockReceived(int pieceIndex, int blockOffset, byte[] data);
}
