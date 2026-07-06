using System.Security.Cryptography;
using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;

namespace bzTorrentClient.Engine.Transfer;

public sealed class RarestFirstPieceManager : IPieceManager
{
    public const int BlockSize = 16 * 1024;

    private readonly ITorrentStorage _storage;
    private readonly List<byte[]> _pieceHashes;
    private readonly bool[] _completed;
    private readonly int[] _availability;
    private readonly Dictionary<int, bool[]> _peerBitfields = new();
    private readonly Dictionary<int, PieceProgress> _inProgress = new();
    private readonly object _lock = new();

    public RarestFirstPieceManager(IMetadata metadata, ITorrentStorage storage, bool[]? initialCompletion = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _pieceHashes = metadata.PieceHashes.ToList();
        _completed = (bool[]?)initialCompletion?.Clone() ?? new bool[_pieceHashes.Count];
        _availability = new int[_pieceHashes.Count];
    }

    public bool IsComplete => _completed.Length > 0 && Array.TrueForAll(_completed, done => done);

    public bool IsPieceComplete(int pieceIndex) => pieceIndex >= 0 && pieceIndex < _completed.Length && _completed[pieceIndex];

    public void RegisterPeerBitfield(int peerId, bool[] bitfield)
    {
        lock (_lock)
        {
            _peerBitfields[peerId] = (bool[])bitfield.Clone();
            for (var i = 0; i < bitfield.Length && i < _availability.Length; i++)
            {
                if (bitfield[i])
                    _availability[i]++;
            }
        }
    }

    public void RegisterPeerHave(int peerId, int pieceIndex)
    {
        lock (_lock)
        {
            if (!_peerBitfields.TryGetValue(peerId, out var bitfield))
            {
                bitfield = new bool[_pieceHashes.Count];
                _peerBitfields[peerId] = bitfield;
            }

            if (pieceIndex < 0 || pieceIndex >= bitfield.Length || bitfield[pieceIndex])
                return;

            bitfield[pieceIndex] = true;
            _availability[pieceIndex]++;
        }
    }

    public void UnregisterPeer(int peerId)
    {
        lock (_lock)
        {
            if (!_peerBitfields.Remove(peerId, out var bitfield))
                return;

            for (var i = 0; i < bitfield.Length; i++)
            {
                if (bitfield[i])
                    _availability[i]--;
            }
        }
    }

    public BlockRequest? TryGetNextRequest(int peerId, bool[] peerBitfield)
    {
        lock (_lock)
        {
            foreach (var (pieceIndex, progress) in _inProgress)
            {
                if (_completed[pieceIndex])
                    continue;
                if (pieceIndex >= peerBitfield.Length || !peerBitfield[pieceIndex])
                    continue;

                var next = NextUnrequestedBlock(pieceIndex, progress);
                if (next is not null)
                    return next;
            }

            var candidate = -1;
            var bestAvailability = int.MaxValue;
            for (var i = 0; i < _pieceHashes.Count; i++)
            {
                if (_completed[i] || _inProgress.ContainsKey(i))
                    continue;
                if (i >= peerBitfield.Length || !peerBitfield[i])
                    continue;
                if (_availability[i] == 0)
                    continue;

                if (_availability[i] < bestAvailability)
                {
                    bestAvailability = _availability[i];
                    candidate = i;
                }
            }

            if (candidate < 0)
                return null;

            var progressState = new PieceProgress(BlockCountFor(candidate));
            _inProgress[candidate] = progressState;
            return NextUnrequestedBlock(candidate, progressState);
        }
    }

    public int? OnBlockReceived(int pieceIndex, int blockOffset, byte[] data)
    {
        lock (_lock)
        {
            if (_completed[pieceIndex])
                return null;

            _storage.WriteBlock(pieceIndex, blockOffset, data);

            if (!_inProgress.TryGetValue(pieceIndex, out var progress))
            {
                progress = new PieceProgress(BlockCountFor(pieceIndex));
                _inProgress[pieceIndex] = progress;
            }

            var blockIndex = blockOffset / BlockSize;
            if (blockIndex >= 0 && blockIndex < progress.Received.Length)
            {
                progress.Requested[blockIndex] = true;
                progress.Received[blockIndex] = true;
            }

            if (!Array.TrueForAll(progress.Received, received => received))
                return null;

            _inProgress.Remove(pieceIndex);

            var pieceData = _storage.ReadPiece(pieceIndex);
            var actualHash = SHA1.HashData(pieceData);
            var expectedHash = _pieceHashes[pieceIndex];

            if (!actualHash.AsSpan().SequenceEqual(expectedHash))
                return null;

            _completed[pieceIndex] = true;
            return pieceIndex;
        }
    }

    private int BlockCountFor(int pieceIndex) =>
        (int)Math.Ceiling(_storage.GetPieceLength(pieceIndex) / (double)BlockSize);

    private BlockRequest? NextUnrequestedBlock(int pieceIndex, PieceProgress progress)
    {
        for (var i = 0; i < progress.Requested.Length; i++)
        {
            if (progress.Requested[i])
                continue;

            progress.Requested[i] = true;
            var offset = i * BlockSize;
            var length = (int)Math.Min(BlockSize, _storage.GetPieceLength(pieceIndex) - offset);
            return new BlockRequest(pieceIndex, offset, length);
        }

        return null;
    }

    private sealed class PieceProgress
    {
        public bool[] Requested { get; }
        public bool[] Received { get; }

        public PieceProgress(int blockCount)
        {
            Requested = new bool[blockCount];
            Received = new bool[blockCount];
        }
    }
}
