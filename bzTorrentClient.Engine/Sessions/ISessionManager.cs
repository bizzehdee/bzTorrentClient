namespace bzTorrentClient.Engine.Sessions;

public interface ISessionManager
{
    IReadOnlyCollection<TorrentSession> Sessions { get; }

    /// <summary>Configured global cap on simultaneous peer connections.</summary>
    int GlobalConnectionBudget { get; }

    /// <summary>Connections currently reserved by peer-connection managers.</summary>
    int ReservedConnections { get; }

    /// <summary>Loads previously persisted sessions on application startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new torrent. It always lands in <see cref="TorrentState.Paused"/>
    /// first; pass <paramref name="startImmediately"/> = false for "add
    /// paused", true to start right away.
    /// </summary>
    Task<TorrentSession> AddAsync(
        TorrentAddSource source,
        string? downloadDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Attempts to reserve <paramref name="count"/> connections against the global budget.</summary>
    bool TryReserveConnections(int count);

    /// <summary>Releases connections previously reserved via <see cref="TryReserveConnections"/>.</summary>
    void ReleaseConnections(int count);
}
