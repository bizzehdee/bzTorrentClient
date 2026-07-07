using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Persistence;

public class SqliteSchemaUpgraderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bztorrentclient-schema-{Guid.NewGuid():N}.db");

    private static readonly string[] SeedUntilColumns =
    {
        "TotalBytesUploaded",
        "TotalBytesDownloaded",
        "SeedingElapsedBeforeThisRunTicks",
        "CurrentSeedingStartedAtUtc",
        "SeedingLimitReached",
    };

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // Pooling=False so disposing a DbContext's connection actually releases the file handle
    // rather than parking it in Microsoft.Data.Sqlite's connection pool. On Windows a pooled
    // (still-open) handle makes File.Delete in Dispose fail with "being used by another
    // process"; Linux allows unlinking an open file, which is why this only surfaced on Windows.
    private DbContextOptions<BzTorrentClientDbContext> Options() =>
        new DbContextOptionsBuilder<BzTorrentClientDbContext>().UseSqlite($"Data Source={_dbPath};Pooling=False").Options;

    /// <summary>
    /// Builds a database via the real schema/EF write path (so column types/formats are
    /// exactly what EF itself produces, not a hand-guessed approximation), then drops the
    /// seed-until columns to simulate a database saved before they existed.
    /// </summary>
    private async Task<Guid> CreateLegacyDatabaseWithOneRowAsync()
    {
        using (var db = new BzTorrentClientDbContext(Options()))
            db.Database.EnsureCreated();

        var store = new EfSessionStore(Options());
        var source = TorrentAddSource.Magnet.FromInfoHash("0123456789abcdef0123456789abcdef01234567");
        var session = new TorrentSession(source, source.ResolveMetadata(), "/downloads");
        await store.SaveAsync(session);

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        foreach (var column in SeedUntilColumns)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE \"TorrentSessions\" DROP COLUMN \"{column}\";";
            drop.ExecuteNonQuery();
        }

        return session.Id;
    }

    [Fact]
    public async Task EnsureColumnsExist_LegacyDatabase_AddsMissingColumnsAndPreservesExistingRows()
    {
        var sessionId = await CreateLegacyDatabaseWithOneRowAsync();

        using (var db = new BzTorrentClientDbContext(Options()))
        {
            SqliteSchemaUpgrader.EnsureColumnsExist(db);
        }

        // The real symptom this fixes: loading a legacy database used to throw
        // "SQLite Error: no such column" the moment EF tried to read a column the model
        // has but the on-disk table didn't.
        var store = new EfSessionStore(Options());
        var sessions = await store.LoadAllAsync();

        var session = Assert.Single(sessions);
        Assert.Equal(sessionId, session.Id);
        Assert.Equal("/downloads", session.DownloadDirectory);
        Assert.Equal(0, session.TotalBytesUploaded);
        Assert.Equal(0, session.TotalBytesDownloaded);
        Assert.Equal(TimeSpan.Zero, session.SeedingElapsedBeforeThisRun);
        Assert.Null(session.CurrentSeedingStartedAtUtc);
        Assert.False(session.SeedingLimitReached);
    }

    [Fact]
    public async Task EnsureColumnsExist_ThenSave_PersistsNewColumnsCorrectly()
    {
        var sessionId = await CreateLegacyDatabaseWithOneRowAsync();

        using (var db = new BzTorrentClientDbContext(Options()))
        {
            SqliteSchemaUpgrader.EnsureColumnsExist(db);
        }

        var store = new EfSessionStore(Options());
        var session = Assert.Single(await store.LoadAllAsync());
        session.AddTransferredBytes(uploaded: 123, downloaded: 456);

        await store.SaveAsync(session);
        var reloaded = Assert.Single(await store.LoadAllAsync());

        Assert.Equal(sessionId, reloaded.Id);
        Assert.Equal(123, reloaded.TotalBytesUploaded);
        Assert.Equal(456, reloaded.TotalBytesDownloaded);
    }

    [Fact]
    public void EnsureColumnsExist_FreshDatabase_IsANoOp()
    {
        using var db = new BzTorrentClientDbContext(Options());
        db.Database.EnsureCreated();

        var exception = Record.Exception(() => SqliteSchemaUpgrader.EnsureColumnsExist(db));

        Assert.Null(exception);
    }
}
