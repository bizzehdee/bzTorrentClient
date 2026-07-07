using System.Net;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Testing;

/// <summary>In-memory fake of <see cref="ISessionManager"/> for view-model tests — no persistence, no networking.</summary>
internal sealed class FakeSessionManager : ISessionManager, ITorrentRuntimeInfoProvider
{
    private readonly List<TorrentSession> _sessions = new();

    public List<(string Method, Guid Id)> Calls { get; } = new();
    public Dictionary<Guid, int> PeerCounts { get; } = new();
    public Dictionary<Guid, List<PeerConnectionInfo>> ConnectedPeers { get; } = new();
    public Dictionary<Guid, TorrentNetworkStats> NetworkStats { get; } = new();

    /// <summary>When set, <see cref="StartAsync"/> delays this long before completing for <see cref="SlowStartSessionId"/>.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.Zero;
    public Guid? SlowStartSessionId { get; set; }

    public IReadOnlyCollection<TorrentSession> Sessions => _sessions;
    public int GlobalConnectionBudget => 200;
    public int ReservedConnections => 0;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TorrentSession> AddAsync(TorrentAddSource source, string? downloadDirectory, bool startImmediately, CancellationToken cancellationToken = default)
    {
        var metadata = source.ResolveMetadata();
        var session = new TorrentSession(source, metadata, string.IsNullOrWhiteSpace(downloadDirectory) ? "/downloads" : downloadDirectory);
        _sessions.Add(session);
        if (startImmediately)
            session.Start();

        return Task.FromResult(session);
    }

    public Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Remove", sessionId));
        _sessions.RemoveAll(s => s.Id == sessionId);
        return Task.CompletedTask;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Start", sessionId));

        if (SlowStartSessionId == sessionId && StartDelay > TimeSpan.Zero)
            await Task.Delay(StartDelay, cancellationToken);

        _sessions.First(s => s.Id == sessionId).Start();
    }

    public Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Pause", sessionId));
        _sessions.First(s => s.Id == sessionId).Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Stop", sessionId));
        _sessions.First(s => s.Id == sessionId).Stop();
        return Task.CompletedTask;
    }

    public Task SaveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Save", sessionId));
        return Task.CompletedTask;
    }

    public bool TryReserveConnections(int count) => true;

    public void ReleaseConnections(int count)
    {
    }

    public int GetActiveConnectionCount(Guid sessionId) => PeerCounts.GetValueOrDefault(sessionId);

    public IReadOnlyCollection<PeerConnectionInfo> GetConnectedPeers(Guid sessionId) =>
        ConnectedPeers.TryGetValue(sessionId, out var peers) ? peers : Array.Empty<PeerConnectionInfo>();

    public TorrentNetworkStats GetNetworkStats(Guid sessionId) =>
        NetworkStats.TryGetValue(sessionId, out var stats) ? stats : TorrentNetworkStats.Empty;
}
