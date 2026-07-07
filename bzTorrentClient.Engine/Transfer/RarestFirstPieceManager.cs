using System.Security.Cryptography;
using bzTorrent.Data;
using bzTorrentClient.Engine.Storage;

namespace bzTorrentClient.Engine.Transfer;

/// <summary>
/// Picks new pieces most-available-first during ramp-up, then rarest-first for the
/// remainder (see <see cref="RampUpPieceGoal"/>), falling back to sequential (lowest
/// index) whenever availability data ties or can't otherwise differentiate candidates. A
/// piece already in progress can be finished by any peer that has it, not just whichever
/// peer started it.
/// </summary>
public sealed class RarestFirstPieceManager : IPieceManager
{
    public const int BlockSize = 16 * 1024;

    /// <summary>
    /// While fewer than this many pieces are complete, new pieces are picked by highest
    /// availability rather than rarest - a handful of common, easy-to-get pieces from many
    /// peers gets data (and therefore other peers' interest in us) flowing fast. Once past
    /// this ramp-up, picking stays on rarest-first for swarm health (scarce pieces don't
    /// disappear if their few holders leave).
    /// </summary>
    private const int RampUpPieceGoal = 4;

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

    public event Action<int>? PieceCompleted;

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
            // Release any blocks this peer had an outstanding request for but never delivered
            // (it dropped mid-piece), so another peer can be offered them. Without this the
            // blocks stay flagged "requested" forever and the partially-downloaded piece never
            // finishes. Received blocks are left alone - they're done, just not by this peer.
            foreach (var progress in _inProgress.Values)
            {
                for (var i = 0; i < progress.RequestedBy.Length; i++)
                {
                    if (progress.RequestedBy[i] == peerId && !progress.Received[i])
                        progress.RequestedBy[i] = NotRequested;
                }
            }

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
            // A piece already in progress isn't reserved for whichever peer started it -
            // any peer that has it can supply any of its still-unrequested blocks, so a
            // single piece can be assembled from many peers concurrently.
            foreach (var (pieceIndex, progress) in _inProgress)
            {
                if (_completed[pieceIndex])
                    continue;
                if (pieceIndex >= peerBitfield.Length || !peerBitfield[pieceIndex])
                    continue;

                var next = NextUnrequestedBlock(pieceIndex, progress, peerId);
                if (next is not null)
                    return next;
            }

            var candidate = SelectNewPieceCandidate(peerBitfield);
            if (candidate < 0)
                return null;

            var progressState = new PieceProgress(BlockCountFor(candidate));
            _inProgress[candidate] = progressState;
            return NextUnrequestedBlock(candidate, progressState, peerId);
        }
    }

    /// <summary>
    /// Picks most-available during ramp-up or rarest-first afterward (see
    /// <see cref="RampUpPieceGoal"/>). Ties within either ranking - most commonly "nothing
    /// has recorded availability data yet differentiating them" - resolve to the
    /// lowest-index candidate, i.e. sequential order, since the scan below runs low-to-high
    /// and only replaces the current candidate on a strictly better availability count.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private int SelectNewPieceCandidate(bool[] peerBitfield)
    {
        var completedCount = 0;
        foreach (var done in _completed)
        {
            if (done)
                completedCount++;
        }

        var rampingUp = completedCount < Math.Min(RampUpPieceGoal, _pieceHashes.Count);
        return SelectByAvailability(peerBitfield, mostAvailableFirst: rampingUp);
    }

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private int SelectByAvailability(bool[] peerBitfield, bool mostAvailableFirst)
    {
        var candidate = -1;
        var best = mostAvailableFirst ? 0 : int.MaxValue;

        for (var i = 0; i < _pieceHashes.Count; i++)
        {
            if (_completed[i] || _inProgress.ContainsKey(i))
                continue;
            if (i >= peerBitfield.Length || !peerBitfield[i])
                continue;
            if (_availability[i] == 0)
                continue;

            if (mostAvailableFirst ? _availability[i] > best : _availability[i] < best)
            {
                best = _availability[i];
                candidate = i;
            }
        }

        return candidate;
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
                // A block can arrive from a peer we never requested it from, or after its
                // original requester dropped and it was re-offered - either way it's done now.
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
        }

        // Raised outside the lock: this manager's own state is already consistent by
        // this point, and the subscriber (TorrentSession, via NetworkedSessionManager)
        // must not run under this manager's lock.
        PieceCompleted?.Invoke(pieceIndex);
        return pieceIndex;
    }

    private int BlockCountFor(int pieceIndex) =>
        (int)Math.Ceiling(_storage.GetPieceLength(pieceIndex) / (double)BlockSize);

    private BlockRequest? NextUnrequestedBlock(int pieceIndex, PieceProgress progress, int peerId)
    {
        for (var i = 0; i < progress.RequestedBy.Length; i++)
        {
            // Offer only blocks that aren't already received and don't have an outstanding
            // request. A block released by a dropped peer (RequestedBy reset to NotRequested)
            // becomes offerable again here.
            if (progress.Received[i] || progress.RequestedBy[i] != NotRequested)
                continue;

            progress.RequestedBy[i] = peerId;
            var offset = i * BlockSize;
            var length = (int)Math.Min(BlockSize, _storage.GetPieceLength(pieceIndex) - offset);
            return new BlockRequest(pieceIndex, offset, length);
        }

        return null;
    }

    /// <summary>Sentinel for a block that has no outstanding request (never requested, or released when its requester dropped). Peer ids are always positive.</summary>
    private const int NotRequested = -1;

    private sealed class PieceProgress
    {
        /// <summary>Per block: the peer id with an outstanding request for it, or <see cref="NotRequested"/>.</summary>
        public int[] RequestedBy { get; }
        public bool[] Received { get; }

        public PieceProgress(int blockCount)
        {
            RequestedBy = new int[blockCount];
            Array.Fill(RequestedBy, NotRequested);
            Received = new bool[blockCount];
        }
    }
}
