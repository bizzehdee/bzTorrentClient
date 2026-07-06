using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using bzTorrentClient.Avalonia.Shell;
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
            }

            var sessionStore = new EfSessionStore(dbOptions);
            var plainSessionManager = new SessionManager(sessionStore, settings);
            var sessionManager = new NetworkedSessionManager(plainSessionManager, settings, GenerateLocalPeerId());
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

    private static string GenerateLocalPeerId()
    {
        var random = new Random();
        var idBuilder = new StringBuilder("-bz0100-");
        for (var i = 0; i < 12; i++)
            idBuilder.Append(random.Next(0, 10));

        return idBuilder.ToString();
    }
}
