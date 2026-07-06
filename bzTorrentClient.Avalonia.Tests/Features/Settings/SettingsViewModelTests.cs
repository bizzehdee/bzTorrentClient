using bzTorrentClient.Avalonia.Features.Settings;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Avalonia.Tests.Features.Settings;

file sealed class FakeClientSettingsStore : IClientSettingsStore
{
    public IClientSettings? Saved { get; private set; }

    public IClientSettings Load() => new ClientSettings();

    public void Save(IClientSettings settings) => Saved = settings;
}

public class SettingsViewModelTests
{
    [Fact]
    public void Save_ValidValues_PersistsAndRaisesSaved()
    {
        var settings = new ClientSettings("/downloads");
        var store = new FakeClientSettingsStore();
        var viewModel = new SettingsViewModel(settings, store)
        {
            DefaultDownloadDirectory = "/new/downloads",
            GlobalMaxConnections = 100,
            MaxConnectionsPerTorrent = 20,
            ListenPort = 7000,
        };

        var saved = false;
        viewModel.Saved += (_, _) => saved = true;

        viewModel.SaveCommand.Execute(null);

        Assert.True(saved);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("/new/downloads", settings.DefaultDownloadDirectory);
        Assert.Equal(100, settings.GlobalMaxConnections);
        Assert.NotNull(store.Saved);
    }

    [Theory]
    [InlineData("", 10, 10, 6881)]
    [InlineData("/downloads", 0, 10, 6881)]
    [InlineData("/downloads", 10, 0, 6881)]
    [InlineData("/downloads", 10, 10, 0)]
    [InlineData("/downloads", 10, 10, 70000)]
    public void Save_InvalidValues_SetsErrorAndDoesNotPersist(string dir, int globalMax, int perTorrent, int port)
    {
        var settings = new ClientSettings("/downloads");
        var store = new FakeClientSettingsStore();
        var viewModel = new SettingsViewModel(settings, store)
        {
            DefaultDownloadDirectory = dir,
            GlobalMaxConnections = globalMax,
            MaxConnectionsPerTorrent = perTorrent,
            ListenPort = port,
        };

        var saved = false;
        viewModel.Saved += (_, _) => saved = true;

        viewModel.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Null(store.Saved);
    }
}
