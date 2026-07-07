using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Features.TorrentList;

public partial class TorrentRowViewModel : ViewModelBase
{
    /// <summary>Speeds are only meaningful once two samples exist at least this far apart, to avoid a noisy first reading.</summary>
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISessionManager _sessionManager;

    public Guid Id { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private TorrentState _state;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private int _peerCount;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    /// <summary>Total torrent size in bytes — for sorting; <see cref="SizeText"/> is the formatted display value.</summary>
    [ObservableProperty]
    private long _sizeBytes;

    /// <summary>When this torrent was added to the client — the "default" sort order.</summary>
    public DateTime AddedAtUtc { get; private set; }

    [ObservableProperty]
    private string _downloadSpeedText = "—";

    [ObservableProperty]
    private string _uploadSpeedText = "—";

    [ObservableProperty]
    private string? _errorMessage;

    private long _lastBytesDownloaded;
    private long _lastBytesUploaded;
    private DateTime _lastSampleTimeUtc;

    // Each row owns its own command instances (rather than sharing one command across
    // all rows via CommandParameter) so that AsyncRelayCommand's built-in re-entrancy
    // guard — CanExecute returns false while IsRunning — scopes to this torrent only.
    // With a shared command, one row's slow operation (e.g. a magnet metadata fetch)
    // used to disable the button for every other row too, and Stop couldn't re-enable
    // Start because they were different commands whose shared instance was busy.
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    public TorrentRowViewModel(Guid id, ISessionManager sessionManager)
    {
        Id = id;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

        StartCommand = new AsyncRelayCommand(() => RunAsync(() => _sessionManager.StartAsync(Id)), CanStart);
        PauseCommand = new AsyncRelayCommand(() => RunAsync(() => _sessionManager.PauseAsync(Id)), CanPause);
        StopCommand = new AsyncRelayCommand(() => RunAsync(() => _sessionManager.StopAsync(Id)), CanStop);
        RemoveCommand = new AsyncRelayCommand(() => RunAsync(() => _sessionManager.RemoveAsync(Id)));
    }

    // Mirrors TorrentSession.Start/Pause/Stop's actual transition rules (see TorrentSession.cs):
    // Start is a no-op when already Active/Seeding and throws during Checking; Pause only
    // makes sense from Active/Seeding; Stop is legal from anywhere but pointless when
    // already Stopped. Hiding those cases instead of letting them no-op or throw keeps the
    // button state honest about what will actually happen.
    private bool CanStart() => State is TorrentState.Paused or TorrentState.Stopped or TorrentState.Error or TorrentState.Completed;
    private bool CanPause() => State is TorrentState.Active or TorrentState.Seeding;
    private bool CanStop() => State is not TorrentState.Stopped;

    partial void OnStateChanged(TorrentState value)
    {
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A torrent operation failing (a bad tracker URL, a disk error, ...) must surface
    /// here, not crash the whole app — CommunityToolkit's AsyncRelayCommand rethrows
    /// unhandled exceptions on the UI thread by default, which is fatal for an unguarded command.
    /// </summary>
    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            ErrorMessage = null;
            await operation();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void UpdateFrom(TorrentSession session, TorrentNetworkStats stats)
    {
        Name = string.IsNullOrWhiteSpace(session.Metadata.Name) ? "(fetching metadata…)" : session.Metadata.Name;
        State = session.State;
        ProgressPercent = Math.Round(session.ProgressFraction * 100, 1);
        PeerCount = stats.ActiveConnections;
        AddedAtUtc = session.AddedAtUtc;

        // "Downloaded" for display is verified progress (pieces confirmed on disk), not the
        // raw byte counter used for speed below — that one also counts bytes from pieces that
        // later failed hash verification and had to be re-requested.
        var totalBytes = session.Metadata.GetFileInfos().Sum(f => f.FileSize);
        SizeBytes = totalBytes;
        SizeText = $"{ByteFormat.Bytes(session.ProgressFraction * totalBytes)} / {ByteFormat.Bytes(totalBytes)}";

        UpdateSpeeds(stats);
    }

    private void UpdateSpeeds(TorrentNetworkStats stats)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastSampleTimeUtc;

        if (_lastSampleTimeUtc != default && elapsed >= MinSampleInterval)
        {
            var downloadDelta = Math.Max(0, stats.BytesDownloaded - _lastBytesDownloaded);
            var uploadDelta = Math.Max(0, stats.BytesUploaded - _lastBytesUploaded);
            DownloadSpeedText = ByteFormat.Rate(downloadDelta / elapsed.TotalSeconds);
            UploadSpeedText = ByteFormat.Rate(uploadDelta / elapsed.TotalSeconds);

            _lastBytesDownloaded = stats.BytesDownloaded;
            _lastBytesUploaded = stats.BytesUploaded;
            _lastSampleTimeUtc = now;
        }
        else if (_lastSampleTimeUtc == default)
        {
            _lastBytesDownloaded = stats.BytesDownloaded;
            _lastBytesUploaded = stats.BytesUploaded;
            _lastSampleTimeUtc = now;
        }
    }
}
