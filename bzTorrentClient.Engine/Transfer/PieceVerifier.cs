using System.Security.Cryptography;
using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;

namespace bzTorrentClient.Engine.Transfer;

/// <summary>
/// Hashes whatever's already on disk against a torrent's piece hashes, so a resumed
/// session only re-fetches pieces it's actually missing instead of trusting a
/// possibly-stale persisted completion bitfield.
/// </summary>
public static class PieceVerifier
{
    public static bool[] Verify(IMetadata metadata, ITorrentStorage storage)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(storage);

        var pieceHashes = metadata.PieceHashes;
        var completion = new bool[pieceHashes.Count];
        var index = 0;

        foreach (var expectedHash in pieceHashes)
        {
            var pieceData = storage.ReadPiece(index);
            var actualHash = SHA1.HashData(pieceData);
            completion[index] = actualHash.AsSpan().SequenceEqual(expectedHash);
            index++;
        }

        return completion;
    }
}
