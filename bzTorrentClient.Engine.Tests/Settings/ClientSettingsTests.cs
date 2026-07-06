using bzTorrentClient.Engine.Settings;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Settings;

public class ClientSettingsTests
{
    [Fact]
    public void DefaultConstructor_UsesPlatformDownloadsDirectory()
    {
        var settings = new ClientSettings();
        Assert.Equal(ClientSettings.GetPlatformDefaultDownloadDirectory(), settings.DefaultDownloadDirectory);
    }

    [Fact]
    public void Constructor_WithExplicitDirectory_UsesIt()
    {
        var settings = new ClientSettings("/custom/downloads");
        Assert.Equal("/custom/downloads", settings.DefaultDownloadDirectory);
    }

    [Fact]
    public void Defaults_MatchStandingPlanDecisions()
    {
        var settings = new ClientSettings();
        Assert.Equal(6881, settings.ListenPort);
        Assert.True(settings.GlobalMaxConnections > 0);
        Assert.True(settings.MaxConnectionsPerTorrent > 0);
    }

    [Fact]
    public void Defaults_SpeedLimitsAreUnlimited()
    {
        var settings = new ClientSettings();
        Assert.Equal(0, settings.GlobalDownloadLimitBytesPerSecond);
        Assert.Equal(0, settings.GlobalUploadLimitBytesPerSecond);
    }
}
