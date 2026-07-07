using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using bzTorrentClient.Avalonia.Shell;
using bzTorrentClient.Engine.Logging;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Persistence;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using Microsoft.EntityFrameworkCore;

namespace bzTorrentClient.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private IClientSettings? _settings;

    public override void OnFrameworkInitializationCompleted()
    {
        // ColorValuesChanged (an async xdg-desktop-portal notification on Linux) keeps the
        // theme following live system changes when the user's setting is Auto; ApplyTheme
        // itself decides whether that's actually the case each time it fires.
        if (PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, values) => ApplyThemeFromPlatformValues(values);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bzTorrentClient");
            Directory.CreateDirectory(appDataDirectory);

            var settingsStore = new JsonClientSettingsStore(Path.Combine(appDataDirectory, "settings.json"));
            var settings = settingsStore.Load();
            _settings = settings;
            ApplyTheme(settings);

            // Pick a fresh listen port for this run if asked - done here, before anything reads
            // settings.ListenPort (the session manager below, and the UPnP mapper), and left in
            // memory only so the configured port isn't overwritten.
            if (settings.RandomiseListenPortOnStartup)
                settings.ListenPort = PickRandomListenPort();

            var dbOptions = new DbContextOptionsBuilder<BzTorrentClientDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appDataDirectory, "sessions.db")}")
                .Options;

            using (var db = new BzTorrentClientDbContext(dbOptions))
            {
                db.Database.EnsureCreated();
                SqliteSchemaUpgrader.EnsureColumnsExist(db);
            }

            var logger = new FileDebugLogger(settings.LogDirectory, settings.LogMaxFileSizeBytes, TimeSpan.FromDays(settings.LogMaxAgeDays));

            // Best-effort router port forwarding (TCP + UDP) for the listen port. Started here
            // and torn down in the shutdown handler below; a router that can't/won't forward is
            // logged and ignored.
            UpnpPortMapper? upnpPortMapper = null;
            if (settings.EnableUpnpPortForwarding)
            {
                upnpPortMapper = new UpnpPortMapper(logger);
                upnpPortMapper.Start(settings.ListenPort);
            }

            var sessionStore = new EfSessionStore(dbOptions);
            var plainSessionManager = new SessionManager(sessionStore, settings);
            var defaultTrackerListProvider = new DefaultTrackerListProvider(
                settings,
                Path.Combine(appDataDirectory, "default-trackers-cache.txt"));
            var ipBlocklistProvider = new IpBlocklistProvider(
                settings,
                Path.Combine(appDataDirectory, "ip-blocklist-cache.txt"));
            var sessionManager = new NetworkedSessionManager(
                plainSessionManager,
                settings,
                GenerateLocalPeerId(),
                defaultTrackerListProvider: defaultTrackerListProvider,
                logger: logger,
                ipBlocklistProvider: ipBlocklistProvider,
                enableInboundListener: true);
            var addPipeline = new TorrentAddPipeline(sessionManager);

            var mainWindowViewModel = new MainWindowViewModel(sessionManager, addPipeline, settings, settingsStore);

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                upnpPortMapper?.Dispose();
                sessionManager.Dispose();
            };

            _ = mainWindowViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Applies <paramref name="settings"/>.ColorTheme: Light/Dark set it directly, Auto
    /// follows the OS the same way startup always did. Called both at launch and whenever
    /// Settings is saved, so a theme change takes effect immediately without a restart.
    /// </summary>
    public void ApplyTheme(IClientSettings settings)
    {
        _settings = settings;

        RequestedThemeVariant = settings.ColorTheme switch
        {
            ColorTheme.Light => ThemeVariant.Light,
            ColorTheme.Dark => ThemeVariant.Dark,
            // RequestedThemeVariant="Default" leaves the initial paint as Light until
            // PlatformSettings' own system-theme subscription (an async xdg-desktop-portal
            // notification on Linux) delivers an update, which can take seconds even though a
            // direct Settings.Read call against the same portal answers in milliseconds
            // (verified via dbus-send). Querying the portal directly up front avoids that
            // flash entirely; ColorValuesChanged keeps following live system changes after.
            _ => TryReadSystemThemeVariantViaPortal()
                ?? (PlatformSettings is { } platformColorSettings ? ToThemeVariant(platformColorSettings.GetColorValues()) : ThemeVariant.Light),
        };
    }

    private void ApplyThemeFromPlatformValues(PlatformColorValues values)
    {
        if (_settings is not { ColorTheme: ColorTheme.Auto })
            return;

        RequestedThemeVariant = ToThemeVariant(values);
    }

    private static ThemeVariant ToThemeVariant(PlatformColorValues colorValues) =>
        colorValues.ThemeVariant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>
    /// Directly asks the xdg-desktop-portal (present on both GNOME and KDE) for the current
    /// color-scheme preference, synchronously and with a hard timeout. Returns null - falling
    /// back to Avalonia's own (possibly stale) PlatformSettings snapshot - if dbus-send isn't
    /// available or the portal doesn't answer in time, e.g. non-Linux or headless setups.
    /// </summary>
    private static ThemeVariant? TryReadSystemThemeVariantViaPortal()
    {
        try
        {
            var startInfo = new ProcessStartInfo("dbus-send")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add("--print-reply");
            startInfo.ArgumentList.Add("--dest=org.freedesktop.portal.Desktop");
            startInfo.ArgumentList.Add("/org/freedesktop/portal/desktop");
            startInfo.ArgumentList.Add("org.freedesktop.portal.Settings.Read");
            startInfo.ArgumentList.Add("string:org.freedesktop.appearance");
            startInfo.ArgumentList.Add("string:color-scheme");

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(500))
                return null;

            var output = process.StandardOutput.ReadToEnd();

            // Reply payload ends "variant uint32 <n>": 1 = prefer-dark, 2 = prefer-light, 0 = no preference.
            var match = Regex.Match(output, @"uint32\s+(\d+)");
            if (!match.Success)
                return null;

            return match.Groups[1].Value == "1" ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A port from the IANA dynamic/ephemeral range (49152-65535), avoiding the
    /// well-known and registered ranges so a randomised listen port doesn't collide with a
    /// service port.</summary>
    private static int PickRandomListenPort() => new Random().Next(49152, 65536);

    private static string GenerateLocalPeerId()
    {
        var random = new Random();
        var idBuilder = new StringBuilder("-bz0100-");
        for (var i = 0; i < 12; i++)
            idBuilder.Append(random.Next(0, 10));

        return idBuilder.ToString();
    }
}
