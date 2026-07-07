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

    public override void OnFrameworkInitializationCompleted()
    {
        // RequestedThemeVariant="Default" leaves the initial paint as Light until
        // PlatformSettings' own system-theme subscription (an async xdg-desktop-portal
        // notification on Linux) delivers an update, which can take seconds even though a
        // direct Settings.Read call against the same portal answers in milliseconds (verified
        // via dbus-send). Querying the portal directly up front avoids that flash entirely;
        // ColorValuesChanged still keeps the theme following live system changes afterward.
        RequestedThemeVariant = TryReadSystemThemeVariantViaPortal()
            ?? (PlatformSettings is { } platformColorSettings ? ToThemeVariant(platformColorSettings.GetColorValues()) : ThemeVariant.Light);

        if (PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, values) => RequestedThemeVariant = ToThemeVariant(values);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bzTorrentClient");
            Directory.CreateDirectory(appDataDirectory);

            var settingsStore = new JsonClientSettingsStore(Path.Combine(appDataDirectory, "settings.json"));
            var settings = settingsStore.Load();

            var dbOptions = new DbContextOptionsBuilder<BzTorrentClientDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appDataDirectory, "sessions.db")}")
                .Options;

            using (var db = new BzTorrentClientDbContext(dbOptions))
            {
                db.Database.EnsureCreated();
                SqliteSchemaUpgrader.EnsureColumnsExist(db);
            }

            var sessionStore = new EfSessionStore(dbOptions);
            var plainSessionManager = new SessionManager(sessionStore, settings);
            var defaultTrackerListProvider = new DefaultTrackerListProvider(
                settings,
                Path.Combine(appDataDirectory, "default-trackers-cache.txt"));
            var sessionManager = new NetworkedSessionManager(
                plainSessionManager,
                settings,
                GenerateLocalPeerId(),
                defaultTrackerListProvider: defaultTrackerListProvider);
            var addPipeline = new TorrentAddPipeline(sessionManager);

            var mainWindowViewModel = new MainWindowViewModel(sessionManager, addPipeline, settings, settingsStore);

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            desktop.ShutdownRequested += (_, _) => sessionManager.Dispose();

            _ = mainWindowViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
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

    private static string GenerateLocalPeerId()
    {
        var random = new Random();
        var idBuilder = new StringBuilder("-bz0100-");
        for (var i = 0; i < 12; i++)
            idBuilder.Append(random.Next(0, 10));

        return idBuilder.ToString();
    }
}
