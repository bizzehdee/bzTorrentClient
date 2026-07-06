using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace bzTorrentClient.Avalonia.Features.StatusFooter;

/// <summary>App-wide peer-discovery and throughput totals across every active torrent.</summary>
public partial class StatusFooterViewModel : ViewModelBase
{
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISessionManager _sessionManager;
    private readonly ITorrentRuntimeInfoProvider? _runtimeInfoProvider;

    [ObservableProperty]
    private int _dhtNodeCount;

    [ObservableProperty]
    private int _dhtPeersFound;

    [ObservableProperty]
    private int _pexPeersFound;

    [ObservableProperty]
    private int _lanPeersFound;

    [ObservableProperty]
    private int _trackerPeersFound;

    [ObservableProperty]
    private string _downloadSpeedText = "—";

    [ObservableProperty]
    private string _uploadSpeedText = "—";

    private long _lastBytesDownloaded;
    private long _lastBytesUploaded;
    private DateTime _lastSampleTimeUtc;

    public StatusFooterViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _runtimeInfoProvider = sessionManager as ITorrentRuntimeInfoProvider;
    }

    public void Refresh()
    {
        if (_runtimeInfoProvider is null)
            return;

        var stats = _sessionManager.Sessions
            .Select(s => _runtimeInfoProvider.GetNetworkStats(s.Id))
            .ToList();

        DhtNodeCount = stats.Sum(s => s.DhtNodeCount);
        DhtPeersFound = stats.Sum(s => s.DhtPeersFound);
        PexPeersFound = stats.Sum(s => s.PexPeersFound);
        LanPeersFound = stats.Sum(s => s.LanPeersFound);
        TrackerPeersFound = stats.Sum(s => s.TrackerPeersFound);

        UpdateSpeeds(stats.Sum(s => s.BytesDownloaded), stats.Sum(s => s.BytesUploaded));
    }

    private void UpdateSpeeds(long totalBytesDownloaded, long totalBytesUploaded)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastSampleTimeUtc;

        if (_lastSampleTimeUtc != default && elapsed >= MinSampleInterval)
        {
            var downloadDelta = Math.Max(0, totalBytesDownloaded - _lastBytesDownloaded);
            var uploadDelta = Math.Max(0, totalBytesUploaded - _lastBytesUploaded);
            DownloadSpeedText = ByteFormat.Rate(downloadDelta / elapsed.TotalSeconds);
            UploadSpeedText = ByteFormat.Rate(uploadDelta / elapsed.TotalSeconds);

            _lastBytesDownloaded = totalBytesDownloaded;
            _lastBytesUploaded = totalBytesUploaded;
            _lastSampleTimeUtc = now;
        }
        else if (_lastSampleTimeUtc == default)
        {
            _lastBytesDownloaded = totalBytesDownloaded;
            _lastBytesUploaded = totalBytesUploaded;
            _lastSampleTimeUtc = now;
        }
    }
}
