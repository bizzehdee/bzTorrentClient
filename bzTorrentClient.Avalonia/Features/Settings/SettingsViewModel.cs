using bzTorrent.IO;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
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
    private bool _closeToTray;

    [ObservableProperty]
    private bool _randomiseListenPortOnStartup;

    [ObservableProperty]
    private bool _enableUpnpPortForwarding;

    /// <summary>KB/s shown to the user; 0 means unlimited. Converted to/from the underlying bytes/second setting.</summary>
    [ObservableProperty]
    private int _downloadLimitKBps;

    [ObservableProperty]
    private int _uploadLimitKBps;

    [ObservableProperty]
    private string _defaultTrackerListUrl;

    [ObservableProperty]
    private string _defaultTrackerListText;

    [ObservableProperty]
    private int _seedUntilMinutes;

    [ObservableProperty]
    private double _seedUntilRatio;

    [ObservableProperty]
    private ColorTheme _colorTheme;

    [ObservableProperty]
    private bool _enableDht;

    [ObservableProperty]
    private bool _enablePex;

    [ObservableProperty]
    private bool _enableLpd;

    [ObservableProperty]
    private PeerEncryptionMode _encryptionMode;

    [ObservableProperty]
    private AddTorrentState _defaultAddTorrentState;

    [ObservableProperty]
    private string _logDirectory;

    /// <summary>KB shown to the user; converted to/from the underlying bytes setting.</summary>
    [ObservableProperty]
    private int _logMaxFileSizeKB;

    [ObservableProperty]
    private int _logMaxAgeDays;

    [ObservableProperty]
    private string _ipBlocklistUrl;

    [ObservableProperty]
    private string _ipBlocklistFilePath;

    [ObservableProperty]
    private string _ipBlocklistText;

    [ObservableProperty]
    private string? _errorMessage;

    public IReadOnlyList<ColorTheme> ColorThemes { get; } = Enum.GetValues<ColorTheme>();
    public IReadOnlyList<PeerEncryptionMode> EncryptionModes { get; } = Enum.GetValues<PeerEncryptionMode>();
    public IReadOnlyList<AddTorrentState> AddTorrentStates { get; } = Enum.GetValues<AddTorrentState>();

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
        _closeToTray = settings.CloseToTray;
        _randomiseListenPortOnStartup = settings.RandomiseListenPortOnStartup;
        _enableUpnpPortForwarding = settings.EnableUpnpPortForwarding;
        _downloadLimitKBps = (int)(settings.GlobalDownloadLimitBytesPerSecond / 1024);
        _uploadLimitKBps = (int)(settings.GlobalUploadLimitBytesPerSecond / 1024);
        _defaultTrackerListUrl = settings.DefaultTrackerListUrl;
        _defaultTrackerListText = settings.DefaultTrackerListText;
        _seedUntilMinutes = settings.SeedUntilMinutes;
        _seedUntilRatio = settings.SeedUntilRatio;
        _colorTheme = settings.ColorTheme;
        _enableDht = settings.EnableDht;
        _enablePex = settings.EnablePex;
        _enableLpd = settings.EnableLpd;
        _encryptionMode = settings.EncryptionMode;
        _defaultAddTorrentState = settings.DefaultAddTorrentState;
        _logDirectory = settings.LogDirectory;
        _logMaxFileSizeKB = (int)(settings.LogMaxFileSizeBytes / 1024);
        _logMaxAgeDays = settings.LogMaxAgeDays;
        _ipBlocklistUrl = settings.IpBlocklistUrl;
        _ipBlocklistFilePath = settings.IpBlocklistFilePath;
        _ipBlocklistText = settings.IpBlocklistText;

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

        if (DownloadLimitKBps < 0 || UploadLimitKBps < 0)
        {
            ErrorMessage = "Speed limits must not be negative — use 0 for unlimited.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(DefaultTrackerListUrl) && !Uri.TryCreate(DefaultTrackerListUrl, UriKind.Absolute, out _))
        {
            ErrorMessage = "Default tracker list URL must be a valid absolute URL.";
            return;
        }

        if (SeedUntilMinutes <= 0)
        {
            ErrorMessage = "Seed-until-time must be a positive number of minutes.";
            return;
        }

        if (SeedUntilRatio <= 0)
        {
            ErrorMessage = "Seed-until-ratio must be a positive number.";
            return;
        }

        if (string.IsNullOrWhiteSpace(LogDirectory))
        {
            ErrorMessage = "Log directory must not be empty.";
            return;
        }

        if (LogMaxFileSizeKB <= 0)
        {
            ErrorMessage = "Log max file size must be a positive number of KB.";
            return;
        }

        if (LogMaxAgeDays <= 0)
        {
            ErrorMessage = "Log max age must be a positive number of days.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(IpBlocklistUrl) && !Uri.TryCreate(IpBlocklistUrl, UriKind.Absolute, out _))
        {
            ErrorMessage = "IP blocklist URL must be a valid absolute URL.";
            return;
        }

        try
        {
            _settings.DefaultDownloadDirectory = DefaultDownloadDirectory;
            _settings.GlobalMaxConnections = GlobalMaxConnections;
            _settings.MaxConnectionsPerTorrent = MaxConnectionsPerTorrent;
            _settings.ListenPort = ListenPort;
            _settings.CloseToTray = CloseToTray;
            _settings.RandomiseListenPortOnStartup = RandomiseListenPortOnStartup;
            _settings.EnableUpnpPortForwarding = EnableUpnpPortForwarding;
            _settings.GlobalDownloadLimitBytesPerSecond = (long)DownloadLimitKBps * 1024;
            _settings.GlobalUploadLimitBytesPerSecond = (long)UploadLimitKBps * 1024;
            _settings.DefaultTrackerListUrl = DefaultTrackerListUrl.Trim();
            _settings.DefaultTrackerListText = DefaultTrackerListText;
            _settings.SeedUntilMinutes = SeedUntilMinutes;
            _settings.SeedUntilRatio = SeedUntilRatio;
            _settings.ColorTheme = ColorTheme;
            _settings.EnableDht = EnableDht;
            _settings.EnablePex = EnablePex;
            _settings.EnableLpd = EnableLpd;
            _settings.EncryptionMode = EncryptionMode;
            _settings.DefaultAddTorrentState = DefaultAddTorrentState;
            _settings.LogDirectory = LogDirectory.Trim();
            _settings.LogMaxFileSizeBytes = (long)LogMaxFileSizeKB * 1024;
            _settings.LogMaxAgeDays = LogMaxAgeDays;
            _settings.IpBlocklistUrl = IpBlocklistUrl.Trim();
            _settings.IpBlocklistFilePath = IpBlocklistFilePath.Trim();
            _settings.IpBlocklistText = IpBlocklistText;
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
