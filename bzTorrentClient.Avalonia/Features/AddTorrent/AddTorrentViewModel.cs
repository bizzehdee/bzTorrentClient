using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Features.AddTorrent;

public partial class AddTorrentViewModel : ViewModelBase
{
    private readonly TorrentAddPipeline _addPipeline;

    [ObservableProperty]
    private AddTorrentMode _mode = AddTorrentMode.File;

    [ObservableProperty]
    private string _torrentFilePath = string.Empty;

    [ObservableProperty]
    private string _magnetUri = string.Empty;

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private string _downloadDirectory;

    /// <summary>Defaults to true: a newly added torrent lands Paused unless the user opts to start it right away.</summary>
    [ObservableProperty]
    private bool _startPaused = true;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised once the torrent has been added successfully; the view closes the dialog on this.</summary>
    public event EventHandler? Completed;

    public IAsyncRelayCommand AddCommand { get; }

    public AddTorrentViewModel(TorrentAddPipeline addPipeline, IClientSettings settings)
    {
        _addPipeline = addPipeline ?? throw new ArgumentNullException(nameof(addPipeline));
        ArgumentNullException.ThrowIfNull(settings);
        _downloadDirectory = settings.DefaultDownloadDirectory;

        AddCommand = new AsyncRelayCommand(AddAsync);
    }

    [RelayCommand]
    private void SetMode(AddTorrentMode mode) => Mode = mode;

    private async Task AddAsync()
    {
        ErrorMessage = null;

        try
        {
            var startImmediately = !StartPaused;
            switch (Mode)
            {
                case AddTorrentMode.File:
                    await _addPipeline.AddFromFileAsync(TorrentFilePath, DownloadDirectory, startImmediately);
                    break;
                case AddTorrentMode.Magnet:
                    await _addPipeline.AddFromMagnetAsync(MagnetUri, DownloadDirectory, startImmediately);
                    break;
                case AddTorrentMode.InfoHash:
                    await _addPipeline.AddFromInfoHashAsync(InfoHash, DownloadDirectory, startImmediately);
                    break;
            }

            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Broad by design: this is a UI command boundary (validation errors, disk
            // errors, or — if "start immediately" is checked — anything StartAsync's
            // networking setup can throw) must show as a message here, not crash the app.
            ErrorMessage = ex.Message;
        }
    }
}
