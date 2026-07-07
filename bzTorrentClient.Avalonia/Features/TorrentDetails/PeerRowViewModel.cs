using System.Net;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Networking;
using CommunityToolkit.Mvvm.ComponentModel;

namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

/// <summary>
/// One connected peer's live direction/speed, derived by sampling the delta in
/// <see cref="PeerConnectionInfo"/>'s cumulative counters between refreshes - the engine
/// only tracks running totals, same pattern <c>TorrentRowViewModel</c> uses for
/// torrent-level speeds. Must be kept alive across refreshes (not replaced) for that
/// delta to mean anything, so the endpoint is the reconciliation key.
/// </summary>
public sealed partial class PeerRowViewModel : ViewModelBase
{
    /// <summary>Speeds/direction are only meaningful once two samples exist at least this far apart, to avoid a noisy first reading.</summary>
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(500);

    public IPEndPoint EndPoint { get; }

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isUploading;

    [ObservableProperty]
    private string _downloadSpeedText = "—";

    [ObservableProperty]
    private string _uploadSpeedText = "—";

    private long _lastBytesDownloaded;
    private long _lastBytesUploaded;
    private DateTime _lastSampleTimeUtc;

    public PeerRowViewModel(IPEndPoint endPoint)
    {
        EndPoint = endPoint;
    }

    public void UpdateFrom(PeerConnectionInfo info)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastSampleTimeUtc;

        if (_lastSampleTimeUtc != default && elapsed >= MinSampleInterval)
        {
            var downloadDelta = Math.Max(0, info.BytesDownloaded - _lastBytesDownloaded);
            var uploadDelta = Math.Max(0, info.BytesUploaded - _lastBytesUploaded);

            IsDownloading = downloadDelta > 0;
            IsUploading = uploadDelta > 0;
            DownloadSpeedText = ByteFormat.Rate(downloadDelta / elapsed.TotalSeconds);
            UploadSpeedText = ByteFormat.Rate(uploadDelta / elapsed.TotalSeconds);

            _lastBytesDownloaded = info.BytesDownloaded;
            _lastBytesUploaded = info.BytesUploaded;
            _lastSampleTimeUtc = now;
        }
        else if (_lastSampleTimeUtc == default)
        {
            _lastBytesDownloaded = info.BytesDownloaded;
            _lastBytesUploaded = info.BytesUploaded;
            _lastSampleTimeUtc = now;
        }
    }
}
