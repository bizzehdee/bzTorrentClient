using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Tests.Persistence;

/// <summary>Fake <see cref="ISessionStore"/> for tests that exercise <see cref="SessionManager"/> without a real database.</summary>
internal sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<Guid, TorrentSession> _saved = new();

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<TorrentSession>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TorrentSession>>(_saved.Values.ToList());

    public Task SaveAsync(TorrentSession session, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        _saved[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _saved.Remove(sessionId);
        return Task.CompletedTask;
    }

    public void Seed(TorrentSession session) => _saved[session.Id] = session;
}
