using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// Repository abstraction over wherever torrent session state is persisted,
/// so the underlying provider (currently EF Core + SQLite) can be swapped
/// without touching <see cref="Sessions.ISessionManager"/>.
/// </summary>
public interface ISessionStore
{
    Task<IReadOnlyList<TorrentSession>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TorrentSession session, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
