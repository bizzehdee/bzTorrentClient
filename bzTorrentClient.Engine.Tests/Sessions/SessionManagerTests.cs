using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Tests.Persistence;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class SessionManagerTests
{
    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    private static (SessionManager manager, InMemorySessionStore store) CreateManager(
        int globalMaxConnections = 200,
        string? defaultDownloadDirectory = "/downloads")
    {
        var store = new InMemorySessionStore();
        var settings = new ClientSettings(defaultDownloadDirectory) { GlobalMaxConnections = globalMaxConnections };
        return (new SessionManager(store, settings), store);
    }

    [Fact]
    public async Task AddAsync_DefaultsToPausedAndUsesDefaultDownloadDirectory()
    {
        var (manager, _) = CreateManager();

        var session = await manager.AddAsync(Source(), downloadDirectory: null, startImmediately: false);

        Assert.Equal(TorrentState.Paused, session.State);
        Assert.Equal("/downloads", session.DownloadDirectory);
        Assert.Contains(session, manager.Sessions);
    }

    [Fact]
    public async Task AddAsync_WithStartImmediately_LandsInActive()
    {
        var (manager, _) = CreateManager();

        var session = await manager.AddAsync(Source(), downloadDirectory: null, startImmediately: true);

        Assert.Equal(TorrentState.Active, session.State);
    }

    [Fact]
    public async Task AddAsync_DuplicateInfoHash_Throws()
    {
        var (manager, _) = CreateManager();
        await manager.AddAsync(Source(), null, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddAsync(Source(), null, false));
    }

    [Fact]
    public async Task AddAsync_ExplicitDownloadDirectory_OverridesDefault()
    {
        var (manager, _) = CreateManager();

        var session = await manager.AddAsync(Source(), "/custom", false);

        Assert.Equal("/custom", session.DownloadDirectory);
    }

    [Fact]
    public async Task AddAsync_PersistsSession()
    {
        var (manager, store) = CreateManager();

        await manager.AddAsync(Source(), null, false);

        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task StartPauseStop_UpdateSessionState()
    {
        var (manager, _) = CreateManager();
        var session = await manager.AddAsync(Source(), null, false);

        await manager.StartAsync(session.Id);
        Assert.Equal(TorrentState.Active, session.State);

        await manager.PauseAsync(session.Id);
        Assert.Equal(TorrentState.Paused, session.State);

        await manager.StopAsync(session.Id);
        Assert.Equal(TorrentState.Stopped, session.State);
    }

    [Fact]
    public async Task StartAsync_UnknownSession_Throws()
    {
        var (manager, _) = CreateManager();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.StartAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RemoveAsync_StopsAndForgetsSession()
    {
        var (manager, store) = CreateManager();
        var session = await manager.AddAsync(Source(), null, true);

        await manager.RemoveAsync(session.Id);

        Assert.DoesNotContain(session, manager.Sessions);
        Assert.Equal(TorrentState.Stopped, session.State);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task InitializeAsync_LoadsPersistedSessionsFromStore()
    {
        var (manager, store) = CreateManager();
        var preExisting = new TorrentSession(Source("1111111111111111111111111111111111111a"), Source("1111111111111111111111111111111111111a").ResolveMetadata(), "/downloads");
        store.Seed(preExisting);

        await manager.InitializeAsync();

        Assert.Single(manager.Sessions);
    }

    [Fact]
    public void TryReserveConnections_RespectsGlobalBudget()
    {
        var (manager, _) = CreateManager(globalMaxConnections: 10);

        Assert.True(manager.TryReserveConnections(6));
        Assert.True(manager.TryReserveConnections(4));
        Assert.False(manager.TryReserveConnections(1));
        Assert.Equal(10, manager.ReservedConnections);
    }

    [Fact]
    public void ReleaseConnections_FreesUpBudget()
    {
        var (manager, _) = CreateManager(globalMaxConnections: 10);
        manager.TryReserveConnections(10);

        manager.ReleaseConnections(4);

        Assert.Equal(6, manager.ReservedConnections);
        Assert.True(manager.TryReserveConnections(4));
    }
}
