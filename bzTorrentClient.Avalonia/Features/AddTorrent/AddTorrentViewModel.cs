using bzTorrent.Data;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Features.AddTorrent;

public partial class AddTorrentViewModel : ViewModelBase
{
    private readonly TorrentAddPipeline _addPipeline;

    /// <summary>A single field the user pastes a .torrent file path, magnet link, or bare info-hash into - which kind it is gets auto-detected on submit rather than asked for up front.</summary>
    [ObservableProperty]
    private string _input = string.Empty;

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

    private async Task AddAsync()
    {
        ErrorMessage = null;

        try
        {
            var startImmediately = !StartPaused;
            var input = Input.Trim();

            if (MagnetLink.IsMagnetLink(input))
                await _addPipeline.AddFromMagnetAsync(input, DownloadDirectory, startImmediately);
            else if (IsInfoHash(input))
                await _addPipeline.AddFromInfoHashAsync(input, DownloadDirectory, startImmediately);
            else if (File.Exists(input))
                await _addPipeline.AddFromFileAsync(input, DownloadDirectory, startImmediately);
            else
                throw new ArgumentException("Enter a .torrent file path, a magnet link, or a 40-character info-hash.");

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

    private static bool IsInfoHash(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);
}
