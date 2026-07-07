namespace bzTorrentClient.Engine.Sessions;

/// <summary>
/// Optional two-phase startup, implemented by <see cref="NetworkedSessionManager"/>.
/// <see cref="LoadAsync"/> loads persisted sessions quickly (a DB read only - no network
/// calls, no disk hashing), so a UI can show the torrent list immediately.
/// <see cref="ResumeAsync"/> does the slower work that would otherwise block the list from
/// appearing for however long it takes: refreshing the default tracker list, verifying
/// every session's on-disk data against its piece hashes, and auto-resuming torrents that
/// were Active/Completed when the app last closed.
/// </summary>
public interface ITwoPhaseSessionInitializer
{
    Task LoadAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);
}
