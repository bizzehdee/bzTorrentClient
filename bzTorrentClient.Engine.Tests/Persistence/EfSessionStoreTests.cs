using Microsoft.EntityFrameworkCore;
using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Persistence;

public class EfSessionStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EfSessionStore _store;

    public EfSessionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-tests-{Guid.NewGuid():N}.db");
        // Pooling=False so disposing a DbContext's connection actually releases the file
        // handle rather than parking it in Microsoft.Data.Sqlite's connection pool. On
        // Windows a pooled (still-open) handle makes File.Delete in Dispose fail with
        // "being used by another process"; Linux allows unlinking an open file, which is
        // why this only surfaced on Windows.
        var options = new DbContextOptionsBuilder<BzTorrentClientDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        using (var db = new BzTorrentClientDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        _store = new EfSessionStore(options);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static TorrentAddSource.Magnet MagnetSource(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task SaveAndLoadAll_RoundTripsMagnetSession()
    {
        var source = MagnetSource();
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");

        await _store.SaveAsync(session);
        var loaded = await _store.LoadAllAsync();

        var reloaded = Assert.Single(loaded);
        Assert.Equal(session.Id, reloaded.Id);
        Assert.Equal(session.Metadata.HashString, reloaded.Metadata.HashString);
        Assert.Equal(session.DownloadDirectory, reloaded.DownloadDirectory);
        Assert.Equal(session.State, reloaded.State);
        Assert.IsType<TorrentAddSource.Magnet>(reloaded.Source);
    }

    [Fact]
    public async Task SaveAndLoadAll_RoundTripsTorrentFileSession()
    {
        var bytes = File.ReadAllBytes(Path.Combine("TestFiles", "UbuntuTestTorrent.torrent"));
        var source = new TorrentAddSource.TorrentFile(bytes);
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");

        await _store.SaveAsync(session);
        var loaded = await _store.LoadAllAsync();

        var reloaded = Assert.Single(loaded);
        Assert.Equal(session.Metadata.HashString, reloaded.Metadata.HashString);
        Assert.IsType<TorrentAddSource.TorrentFile>(reloaded.Source);
    }

    [Fact]
    public async Task SaveAndLoadAll_RoundTripsPieceCompletionBitfield()
    {
        var bytes = File.ReadAllBytes(Path.Combine("TestFiles", "UbuntuTestTorrent.torrent"));
        var source = new TorrentAddSource.TorrentFile(bytes);
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");
        session.MarkPieceVerified(0);
        session.MarkPieceVerified(2);

        await _store.SaveAsync(session);
        var reloaded = Assert.Single(await _store.LoadAllAsync());

        Assert.True(reloaded.PieceCompletion[0]);
        Assert.False(reloaded.PieceCompletion[1]);
        Assert.True(reloaded.PieceCompletion[2]);
        Assert.Equal(session.PieceCompletion.Length, reloaded.PieceCompletion.Length);
    }

    [Fact]
    public async Task Save_ExistingSession_UpdatesRatherThanDuplicates()
    {
        var source = MagnetSource();
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");
        await _store.SaveAsync(session);

        session.Start();
        await _store.SaveAsync(session);

        var loaded = await _store.LoadAllAsync();
        var reloaded = Assert.Single(loaded);
        Assert.Equal(TorrentState.Active, reloaded.State);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var source = MagnetSource();
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");
        await _store.SaveAsync(session);

        await _store.DeleteAsync(session.Id);

        Assert.Empty(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        await _store.DeleteAsync(Guid.NewGuid());
    }
}
