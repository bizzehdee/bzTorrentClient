using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
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

    // Set when the user explicitly exits via the tray menu, so the main window's Closing
    // handler lets the close through instead of hiding it to the tray.
    private bool _exitRequestedFromTray;

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
            var peerCacheStore = new JsonPeerCacheStore(Path.Combine(appDataDirectory, "peer-cache.json"));
            var dhtNodeStore = new JsonDhtNodeStore(Path.Combine(appDataDirectory, "dht-nodes.json"));
            var sessionManager = new NetworkedSessionManager(
                plainSessionManager,
                settings,
                GenerateLocalPeerId(),
                defaultTrackerListProvider: defaultTrackerListProvider,
                logger: logger,
                ipBlocklistProvider: ipBlocklistProvider,
                enableInboundListener: true,
                peerCacheStore: peerCacheStore,
                dhtNodeStore: dhtNodeStore);
            var addPipeline = new TorrentAddPipeline(sessionManager);

            var mainWindowViewModel = new MainWindowViewModel(sessionManager, addPipeline, settings, settingsStore);

            var mainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };
            desktop.MainWindow = mainWindow;

            var trayIcon = SetUpTrayIcon(mainWindow);

            desktop.ShutdownRequested += (_, _) =>
            {
                trayIcon?.Dispose();
                upnpPortMapper?.Dispose();
                sessionManager.Dispose();
            };

            _ = mainWindowViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the system-tray icon: clicking it (or its Show/Hide menu item) toggles the main
    /// window, and Exit shuts the app down for real. Also wires the main window's close button
    /// to hide-to-tray when the CloseToTray setting is on. Returns the tray icon so the caller
    /// can dispose it on shutdown.
    /// </summary>
    private TrayIcon SetUpTrayIcon(Window mainWindow)
    {
        void ToggleWindow()
        {
            if (mainWindow.IsVisible)
            {
                mainWindow.Hide();
            }
            else
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }

        var showHideItem = new NativeMenuItem("Show / Hide");
        showHideItem.Click += (_, _) => ToggleWindow();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            // Close the (only) window with the flag set so the Closing handler lets it through
            // instead of hiding it. That runs the normal last-window-close shutdown path -
            // same cleanup (persistence, UPnP unmap) as closing with close-to-tray off.
            _exitRequestedFromTray = true;
            mainWindow.Close();
        };

        var menu = new NativeMenu();
        menu.Items.Add(showHideItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://bzTorrentClient.Avalonia/Assets/bztorrent.ico"))),
            ToolTipText = "bzTorrent Client",
            Menu = menu,
            IsVisible = true,
        };
        trayIcon.Clicked += (_, _) => ToggleWindow();

        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });

        // Close-to-tray: intercept the window's close and hide it instead, unless the setting
        // is off or the user chose Exit from the tray. _settings is the same instance the
        // Settings dialog writes to, so toggling it takes effect without a restart.
        mainWindow.Closing += (_, e) =>
        {
            if (!_exitRequestedFromTray && (_settings?.CloseToTray ?? true))
            {
                e.Cancel = true;
                mainWindow.Hide();
            }
        };

        return trayIcon;
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
        // Azureus-style peer id: "-bzMMmm-" then 12 random digits, where MM/mm are the two-digit
        // major/minor of the assembly version (CI stamps it via -p:Version). e.g. version 2.4.1
        // -> "-bz0204-". Mod 100 keeps each field two digits if the version ever exceeds 99.
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0);

        var random = new Random();
        var idBuilder = new StringBuilder($"-bz{version.Major % 100:D2}{version.Minor % 100:D2}-");
        for (var i = 0; i < 12; i++)
            idBuilder.Append(random.Next(0, 10));

        return idBuilder.ToString();
    }
}
