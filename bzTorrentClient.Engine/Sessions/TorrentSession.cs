using bzTorrent.Data;

namespace bzTorrentClient.Engine.Sessions;

public sealed class TorrentSession
{
    public Guid Id { get; }
    public TorrentAddSource Source { get; private set; }
    public IMetadata Metadata { get; }
    public string DownloadDirectory { get; }
    public DateTime AddedAtUtc { get; }
    public TorrentState State { get; private set; }
    public bool[] PieceCompletion { get; private set; }
    public string? LastError { get; private set; }

    public TorrentSession(TorrentAddSource source, IMetadata metadata, string downloadDirectory)
        : this(
            Guid.NewGuid(),
            source,
            metadata,
            downloadDirectory,
            TorrentState.Paused,
            DateTime.UtcNow,
            new bool[metadata?.PieceHashes.Count ?? 0])
    {
    }

    public TorrentSession(
        Guid id,
        TorrentAddSource source,
        IMetadata metadata,
        string downloadDirectory,
        TorrentState state,
        DateTime addedAtUtc,
        bool[] pieceCompletion)
    {
        if (string.IsNullOrWhiteSpace(downloadDirectory))
            throw new ArgumentException("Download directory must not be empty.", nameof(downloadDirectory));

        Id = id;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        DownloadDirectory = downloadDirectory;
        State = state;
        AddedAtUtc = addedAtUtc;
        PieceCompletion = pieceCompletion ?? Array.Empty<bool>();
    }

    public double ProgressFraction =>
        PieceCompletion.Length == 0 ? 0d : (double)PieceCompletion.Count(done => done) / PieceCompletion.Length;

    public bool IsFullyVerified =>
        PieceCompletion.Length > 0 && Array.TrueForAll(PieceCompletion, done => done);

    // --- User-driven lifecycle transitions ---

    /// <summary>
    /// Paused/Stopped/Error -> Active or Seeding (Seeding if everything's already
    /// verified - e.g. resuming a torrent that finished before the app last closed).
    /// Completed -> Seeding (starts seeding an idle-but-fully-downloaded torrent).
    /// No-op if already Active/Seeding.
    /// </summary>
    public void Start()
    {
        switch (State)
        {
            case TorrentState.Paused:
            case TorrentState.Stopped:
            case TorrentState.Error:
                State = IsFullyVerified ? TorrentState.Seeding : TorrentState.Active;
                LastError = null;
                break;
            case TorrentState.Completed:
                State = TorrentState.Seeding;
                break;
            case TorrentState.Active:
            case TorrentState.Seeding:
                break;
            case TorrentState.Checking:
                throw new InvalidOperationException("Cannot start a torrent while it is being checked.");
            default:
                throw new InvalidOperationException($"Unhandled state {State}.");
        }
    }

    /// <summary>
    /// Soft halt: Active/Seeding -> Paused. Peer connections are dropped
    /// but the torrent stays loaded — resuming does not need a fresh
    /// tracker/DHT announce.
    /// </summary>
    public void Pause()
    {
        switch (State)
        {
            case TorrentState.Active:
            case TorrentState.Seeding:
                State = TorrentState.Paused;
                break;
            case TorrentState.Paused:
                break;
            case TorrentState.Stopped:
                throw new InvalidOperationException("Cannot pause a stopped torrent; start it first.");
            case TorrentState.Checking:
                throw new InvalidOperationException("Cannot pause a torrent while it is being checked.");
            case TorrentState.Error:
                throw new InvalidOperationException("Cannot pause a torrent that is in an error state; start it to retry.");
            case TorrentState.Completed:
                throw new InvalidOperationException("Cannot pause a completed torrent that isn't running; start it to seed, or stop it.");
            default:
                throw new InvalidOperationException($"Unhandled state {State}.");
        }
    }

    /// <summary>
    /// Hard halt from any state: tears down the tracker/DHT session too, so
    /// resuming re-announces from scratch. Idempotent.
    /// </summary>
    public void Stop()
    {
        State = TorrentState.Stopped;
    }

    // --- Engine-driven transitions (verification pipeline) ---

    public void BeginChecking()
    {
        if (State is not (TorrentState.Paused or TorrentState.Stopped))
            throw new InvalidOperationException($"Cannot begin checking from state {State}.");

        State = TorrentState.Checking;
    }

    public void FinishChecking()
    {
        if (State != TorrentState.Checking)
            throw new InvalidOperationException($"Cannot finish checking from state {State}.");

        State = IsFullyVerified ? TorrentState.Completed : TorrentState.Paused;
    }

    /// <summary>
    /// Replaces <see cref="PieceCompletion"/> with the result of hashing whatever's
    /// already on disk against the torrent's piece hashes (see <see cref="Transfer.PieceVerifier"/>).
    /// Only valid mid-<see cref="BeginChecking"/>/<see cref="FinishChecking"/>.
    /// </summary>
    public void ApplyVerificationResult(bool[] pieceCompletion)
    {
        if (State != TorrentState.Checking)
            throw new InvalidOperationException($"Cannot apply verification results from state {State}.");

        ArgumentNullException.ThrowIfNull(pieceCompletion);

        if (pieceCompletion.Length != Metadata.PieceHashes.Count)
            throw new ArgumentException("Verification result must cover every piece in the torrent.", nameof(pieceCompletion));

        PieceCompletion = pieceCompletion;
    }

    public void Fail(string reason)
    {
        LastError = reason ?? throw new ArgumentNullException(nameof(reason));
        State = TorrentState.Error;
    }

    public void MarkPieceVerified(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCompletion.Length)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex));

        PieceCompletion[pieceIndex] = true;

        if (State == TorrentState.Active && IsFullyVerified)
            State = TorrentState.Seeding;
    }

    /// <summary>
    /// Call after a magnet/info-hash session's <see cref="Metadata"/> gets its info
    /// dictionary filled in (BEP-9 fetch) — <see cref="PieceCompletion"/> was sized 0
    /// until the piece count was known.
    /// </summary>
    internal void OnMetadataPopulated()
    {
        if (PieceCompletion.Length == Metadata.PieceHashes.Count)
            return;

        PieceCompletion = new bool[Metadata.PieceHashes.Count];
    }

    /// <summary>
    /// After a successful BEP-9 fetch, swap the add source over to the now-fully-known
    /// torrent bytes, so a future restart doesn't need to re-fetch metadata over DHT.
    /// </summary>
    internal void PromoteSourceToTorrentFile(byte[] torrentBytes)
    {
        Source = new TorrentAddSource.TorrentFile(torrentBytes);
    }
}
