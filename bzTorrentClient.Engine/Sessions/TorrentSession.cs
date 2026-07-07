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

    /// <summary>All-time cumulative bytes uploaded/downloaded for this torrent, persisted across restarts - the basis for the seed-until-ratio setting.</summary>
    public long TotalBytesUploaded { get; private set; }
    public long TotalBytesDownloaded { get; private set; }

    /// <summary>Seeding time banked from prior seeding periods (before whatever's currently running), persisted across restarts.</summary>
    public TimeSpan SeedingElapsedBeforeThisRun { get; private set; }

    /// <summary>Set while actively Seeding; null the rest of the time. Persisted so a period of seeding survives an app restart without losing its elapsed time.</summary>
    public DateTime? CurrentSeedingStartedAtUtc { get; private set; }

    /// <summary>
    /// Once the seed-until-time/ratio policy has stopped this torrent automatically, this
    /// is set and stays set - per the user's intent, once they restart seeding after a
    /// limit was reached they mean "seed forever until I manually stop it", not "re-apply
    /// the same limit again".
    /// </summary>
    public bool SeedingLimitReached { get; private set; }

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
        bool[] pieceCompletion,
        long totalBytesUploaded = 0,
        long totalBytesDownloaded = 0,
        TimeSpan? seedingElapsedBeforeThisRun = null,
        DateTime? currentSeedingStartedAtUtc = null,
        bool seedingLimitReached = false)
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
        TotalBytesUploaded = totalBytesUploaded;
        TotalBytesDownloaded = totalBytesDownloaded;
        SeedingElapsedBeforeThisRun = seedingElapsedBeforeThisRun ?? TimeSpan.Zero;
        CurrentSeedingStartedAtUtc = currentSeedingStartedAtUtc;
        SeedingLimitReached = seedingLimitReached;
    }

    public double ProgressFraction =>
        PieceCompletion.Length == 0 ? 0d : (double)PieceCompletion.Count(done => done) / PieceCompletion.Length;

    public bool IsFullyVerified =>
        PieceCompletion.Length > 0 && Array.TrueForAll(PieceCompletion, done => done);

    /// <summary>Total time spent Seeding, across every seeding period this torrent has ever had (including one currently in progress).</summary>
    public TimeSpan TotalSeedingElapsed =>
        SeedingElapsedBeforeThisRun + (CurrentSeedingStartedAtUtc is { } startedAt ? DateTime.UtcNow - startedAt : TimeSpan.Zero);

    /// <summary>All-time upload/download ratio for this torrent, or null if nothing's been downloaded yet (ratio is undefined, not zero or infinite).</summary>
    public double? SeedRatio => TotalBytesDownloaded > 0 ? (double)TotalBytesUploaded / TotalBytesDownloaded : null;

    /// <summary>Adds to the all-time transferred-byte totals - call as new bytes are observed, regardless of current state.</summary>
    public void AddTransferredBytes(long uploaded, long downloaded)
    {
        TotalBytesUploaded += uploaded;
        TotalBytesDownloaded += downloaded;
    }

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

        if (State == TorrentState.Seeding)
            CurrentSeedingStartedAtUtc ??= DateTime.UtcNow;
    }

    /// <summary>
    /// Soft halt: Active/Seeding -> Paused. Peer connections are dropped
    /// but the torrent stays loaded — resuming does not need a fresh
    /// tracker/DHT announce.
    /// </summary>
    public void Pause()
    {
        BankSeedingClock();

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
        BankSeedingClock();
        State = TorrentState.Stopped;
    }

    /// <summary>
    /// Automatic stop triggered by the seed-until-time/ratio policy: Seeding -> Completed.
    /// Marks the per-torrent seed limit as reached, so a later manual <see cref="Start"/>
    /// seeds forever instead of re-applying the same limit.
    /// </summary>
    public void StopSeedingDueToLimit()
    {
        if (State != TorrentState.Seeding)
            throw new InvalidOperationException($"Cannot stop seeding from state {State}.");

        BankSeedingClock();
        SeedingLimitReached = true;
        State = TorrentState.Completed;
    }

    /// <summary>Banks elapsed time from the current seeding period (if any) into <see cref="SeedingElapsedBeforeThisRun"/> and clears the live clock.</summary>
    private void BankSeedingClock()
    {
        if (CurrentSeedingStartedAtUtc is not { } startedAt)
            return;

        SeedingElapsedBeforeThisRun += DateTime.UtcNow - startedAt;
        CurrentSeedingStartedAtUtc = null;
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
        {
            State = TorrentState.Seeding;
            CurrentSeedingStartedAtUtc ??= DateTime.UtcNow;
        }
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
