using Avalonia.Threading;
using bzTorrentClient.Avalonia.Features.AddTorrent;
using bzTorrentClient.Avalonia.Features.Settings;
using bzTorrentClient.Avalonia.Features.StatusFooter;
using bzTorrentClient.Avalonia.Features.TorrentDetails;
using bzTorrentClient.Avalonia.Features.TorrentList;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Shell;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ISessionManager _sessionManager;
    private readonly DispatcherTimer _refreshTimer;

    public TorrentListViewModel TorrentList { get; }
    public TorrentDetailsViewModel Details { get; }
    public StatusFooterViewModel StatusFooter { get; }
    public TorrentAddPipeline AddPipeline { get; }
    public IClientSettings Settings { get; }
    public IClientSettingsStore SettingsStore { get; }

    public IRelayCommand OpenAddTorrentCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }

    /// <summary>The view opens the Add Torrent dialog in response to this (keeps Avalonia Window APIs out of the view model).</summary>
    public event EventHandler? AddTorrentRequested;

    /// <summary>The view opens the Settings dialog in response to this.</summary>
    public event EventHandler? SettingsRequested;

    public MainWindowViewModel(
        ISessionManager sessionManager,
        TorrentAddPipeline addPipeline,
        IClientSettings settings,
        IClientSettingsStore settingsStore)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        AddPipeline = addPipeline ?? throw new ArgumentNullException(nameof(addPipeline));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        TorrentList = new TorrentListViewModel(sessionManager);
        Details = new TorrentDetailsViewModel(sessionManager);
        StatusFooter = new StatusFooterViewModel(sessionManager);
        TorrentList.SelectionChanged += (_, id) => Details.ShowSession(id);

        OpenAddTorrentCommand = new RelayCommand(() => AddTorrentRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));

        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) =>
        {
            TorrentList.Refresh();
            Details.Refresh();
            StatusFooter.Refresh();
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Called fire-and-forget from App.axaml.cs at startup — a failure loading
            // persisted sessions (corrupt DB row, a magnet source that no longer resolves,
            // ...) must not prevent the window from opening at all.
            await _sessionManager.InitializeAsync();
        }
        catch (Exception ex)
        {
            TorrentList.LastErrorMessage = $"Failed to load saved torrents: {ex.Message}";
        }

        TorrentList.Refresh();
        _refreshTimer.Start();
    }
}
