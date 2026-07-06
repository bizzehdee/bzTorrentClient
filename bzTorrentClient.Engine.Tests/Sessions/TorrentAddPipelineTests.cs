using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using bzTorrentClient.Engine.Tests.Persistence;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Sessions;

public class TorrentAddPipelineTests
{
    private static (TorrentAddPipeline pipeline, SessionManager manager) CreatePipeline()
    {
        var store = new InMemorySessionStore();
        var settings = new ClientSettings("/downloads");
        var manager = new SessionManager(store, settings);
        return (new TorrentAddPipeline(manager), manager);
    }

    [Fact]
    public async Task AddFromFileAsync_ReadsFileAndAdds()
    {
        var (pipeline, manager) = CreatePipeline();
        var path = Path.Combine("TestFiles", "UbuntuTestTorrent.torrent");

        var session = await pipeline.AddFromFileAsync(path, null, startImmediately: false);

        Assert.NotEmpty(session.Metadata.PieceHashes);
        Assert.Contains(session, manager.Sessions);
    }

    [Fact]
    public async Task AddFromFileAsync_MissingFile_Throws()
    {
        var (pipeline, _) = CreatePipeline();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            pipeline.AddFromFileAsync("does-not-exist.torrent", null, false));
    }

    [Fact]
    public async Task AddFromMagnetAsync_ValidMagnet_Adds()
    {
        var (pipeline, manager) = CreatePipeline();

        var session = await pipeline.AddFromMagnetAsync(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=example",
            null,
            startImmediately: false);

        Assert.Empty(session.Metadata.PieceHashes);
        Assert.Contains(session, manager.Sessions);
    }

    [Fact]
    public async Task AddFromMagnetAsync_InvalidUri_Throws()
    {
        var (pipeline, _) = CreatePipeline();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            pipeline.AddFromMagnetAsync("not-a-magnet", null, false));
    }

    [Fact]
    public async Task AddFromInfoHashAsync_ValidHash_AddsAsTrackerlessMagnet()
    {
        var (pipeline, manager) = CreatePipeline();

        var session = await pipeline.AddFromInfoHashAsync(
            "0123456789abcdef0123456789abcdef01234567",
            null,
            startImmediately: false);

        Assert.IsType<TorrentAddSource.Magnet>(session.Source);
        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", ((TorrentAddSource.Magnet)session.Source).Uri);
        Assert.Contains(session, manager.Sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task AddFromInfoHashAsync_InvalidHash_Throws(string hash)
    {
        var (pipeline, _) = CreatePipeline();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            pipeline.AddFromInfoHashAsync(hash, null, false));
    }
}
