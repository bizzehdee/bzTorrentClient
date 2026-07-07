using System.Collections.Concurrent;
using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Storage;

namespace bzTorrentClient.Engine.Sessions;

public sealed class SessionManager : ISessionManager
{
    private readonly ConcurrentDictionary<Guid, TorrentSession> _sessions = new();
    private readonly ISessionStore _sessionStore;
    private readonly IClientSettings _settings;
    private int _reservedConnections;

    public SessionManager(ISessionStore sessionStore, IClientSettings settings)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyCollection<TorrentSession> Sessions => _sessions.Values.ToList();

    public int GlobalConnectionBudget => _settings.GlobalMaxConnections;

    public int ReservedConnections => Volatile.Read(ref _reservedConnections);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await _sessionStore.LoadAllAsync(cancellationToken);
        foreach (var session in persisted)
        {
            _sessions[session.Id] = session;
        }
    }

    public async Task<TorrentSession> AddAsync(
        TorrentAddSource source,
        string? downloadDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var metadata = source.ResolveMetadata();

        if (_sessions.Values.Any(s => string.Equals(s.Metadata.HashString, metadata.HashString, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A torrent with info-hash {metadata.HashString} has already been added.");

        var directory = string.IsNullOrWhiteSpace(downloadDirectory)
            ? _settings.DefaultDownloadDirectory
            : downloadDirectory;

        var session = new TorrentSession(source, metadata, directory);
        _sessions[session.Id] = session;
        await _sessionStore.SaveAsync(session, cancellationToken);

        if (startImmediately)
            await StartAsync(session.Id, cancellationToken);

        return session;
    }

    public async Task RemoveAsync(Guid sessionId, bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        session.Stop();
        await _sessionStore.DeleteAsync(sessionId, cancellationToken);

        if (deleteFiles)
            FileSystemTorrentStorage.DeleteFiles(session.Metadata, session.DownloadDirectory);
    }

    public Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, session => session.Start(), cancellationToken);

    public Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, session => session.Pause(), cancellationToken);

    public Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, session => session.Stop(), cancellationToken);

    public Task SaveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, static _ => { }, cancellationToken);

    public bool TryReserveConnections(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        while (true)
        {
            var current = Volatile.Read(ref _reservedConnections);
            var updated = current + count;
            if (updated > _settings.GlobalMaxConnections)
                return false;

            if (Interlocked.CompareExchange(ref _reservedConnections, updated, current) == current)
                return true;
        }
    }

    public void ReleaseConnections(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        Interlocked.Add(ref _reservedConnections, -count);
    }

    private async Task MutateAsync(Guid sessionId, Action<TorrentSession> mutate, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"No torrent session with id {sessionId}.");

        mutate(session);
        await _sessionStore.SaveAsync(session, cancellationToken);
    }
}
