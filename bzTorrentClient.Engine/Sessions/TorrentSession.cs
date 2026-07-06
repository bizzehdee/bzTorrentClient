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
    /// Paused/Stopped/Error -> Active. No-op if already Active/Completed
    /// (Completed still counts as "running" — it just means everything is
    /// verified and the torrent is now seeding).
    /// </summary>
    public void Start()
    {
        switch (State)
        {
            case TorrentState.Paused:
            case TorrentState.Stopped:
            case TorrentState.Error:
                State = TorrentState.Active;
                LastError = null;
                break;
            case TorrentState.Active:
            case TorrentState.Completed:
                break;
            case TorrentState.Checking:
                throw new InvalidOperationException("Cannot start a torrent while it is being checked.");
            default:
                throw new InvalidOperationException($"Unhandled state {State}.");
        }
    }

    /// <summary>
    /// Soft halt: Active/Completed -> Paused. Peer connections are dropped
    /// but the torrent stays loaded — resuming does not need a fresh
    /// tracker/DHT announce.
    /// </summary>
    public void Pause()
    {
        switch (State)
        {
            case TorrentState.Active:
            case TorrentState.Completed:
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
            State = TorrentState.Completed;
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
