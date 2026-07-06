using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Features.Settings;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IClientSettings _settings;
    private readonly IClientSettingsStore _settingsStore;

    [ObservableProperty]
    private string _defaultDownloadDirectory;

    [ObservableProperty]
    private int _globalMaxConnections;

    [ObservableProperty]
    private int _maxConnectionsPerTorrent;

    [ObservableProperty]
    private int _listenPort;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised once settings are validated and persisted; the view closes the dialog on this.</summary>
    public event EventHandler? Saved;

    public IRelayCommand SaveCommand { get; }

    public SettingsViewModel(IClientSettings settings, IClientSettingsStore settingsStore)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        _defaultDownloadDirectory = settings.DefaultDownloadDirectory;
        _globalMaxConnections = settings.GlobalMaxConnections;
        _maxConnectionsPerTorrent = settings.MaxConnectionsPerTorrent;
        _listenPort = settings.ListenPort;

        SaveCommand = new RelayCommand(Save);
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(DefaultDownloadDirectory))
        {
            ErrorMessage = "Download directory must not be empty.";
            return;
        }

        if (GlobalMaxConnections <= 0 || MaxConnectionsPerTorrent <= 0)
        {
            ErrorMessage = "Connection limits must be positive numbers.";
            return;
        }

        if (ListenPort is <= 0 or > 65535)
        {
            ErrorMessage = "Listen port must be between 1 and 65535.";
            return;
        }

        try
        {
            _settings.DefaultDownloadDirectory = DefaultDownloadDirectory;
            _settings.GlobalMaxConnections = GlobalMaxConnections;
            _settings.MaxConnectionsPerTorrent = MaxConnectionsPerTorrent;
            _settings.ListenPort = ListenPort;
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        ErrorMessage = null;
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
