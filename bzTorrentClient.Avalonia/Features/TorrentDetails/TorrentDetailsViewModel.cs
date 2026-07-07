using System.Collections.ObjectModel;
using System.Net;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace bzTorrentClient.Avalonia.Features.TorrentDetails;

public partial class TorrentDetailsViewModel : ViewModelBase
{
    /// <summary>Piece counts routinely run into the thousands; the heat strip buckets down to this many cells so it stays a glance-able strip rather than an unreadable wall of pixels.</summary>
    private const int MaxPieceMapCells = 240;

    private readonly ISessionManager _sessionManager;
    private readonly ITorrentRuntimeInfoProvider? _runtimeInfoProvider;
    private Guid? _sessionId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private string _downloadDirectory = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isFetchingMetadata;

    public ObservableCollection<PeerRowViewModel> Peers { get; } = new();
    public ObservableCollection<FileRowViewModel> Files { get; } = new();
    public ObservableCollection<string> Trackers { get; } = new();
    public ObservableCollection<PieceMapCell> PieceMap { get; } = new();

    public TorrentDetailsViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _runtimeInfoProvider = sessionManager as ITorrentRuntimeInfoProvider;
    }

    public void ShowSession(Guid? sessionId)
    {
        _sessionId = sessionId;
        Refresh();
    }

    public void Refresh()
    {
        var session = _sessionId is null ? null : _sessionManager.Sessions.FirstOrDefault(s => s.Id == _sessionId);
        if (session is null)
        {
            Clear();
            return;
        }

        HasSelection = true;
        IsFetchingMetadata = session.Metadata.PieceHashes.Count == 0;
        Name = string.IsNullOrWhiteSpace(session.Metadata.Name) ? "(fetching metadata…)" : session.Metadata.Name;
        InfoHash = session.Metadata.HashString;
        DownloadDirectory = session.DownloadDirectory;

        ReplaceAll(Trackers, session.Metadata.AnnounceList);
        ReplaceAll(Files, BuildFileRows(session));

        UpdatePeers(session.Id);

        ReplaceAll(PieceMap, BuildPieceMap(session.PieceCompletion));
    }

    /// <summary>
    /// Reconciles in place (rather than <see cref="ReplaceAll{T}"/>) - a peer row must
    /// survive across refreshes for its speed/direction sampling to mean anything.
    /// </summary>
    private void UpdatePeers(Guid sessionId)
    {
        var infos = _runtimeInfoProvider?.GetConnectedPeers(sessionId) ?? Array.Empty<PeerConnectionInfo>();
        var seen = new HashSet<IPEndPoint>();

        foreach (var info in infos)
        {
            seen.Add(info.EndPoint);
            var row = Peers.FirstOrDefault(p => p.EndPoint.Equals(info.EndPoint));
            if (row is null)
            {
                row = new PeerRowViewModel(info.EndPoint);
                Peers.Add(row);
            }

            row.UpdateFrom(info);
        }

        for (var i = Peers.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Peers[i].EndPoint))
                Peers.RemoveAt(i);
        }
    }

    private void Clear()
    {
        HasSelection = false;
        IsFetchingMetadata = false;
        Name = string.Empty;
        InfoHash = string.Empty;
        DownloadDirectory = string.Empty;
        Trackers.Clear();
        Files.Clear();
        Peers.Clear();
        PieceMap.Clear();
    }

    /// <summary>
    /// A piece counts toward every file it overlaps only once fully verified - completion
    /// isn't tracked at sub-piece resolution, so this is the same approximation real
    /// torrent clients use for per-file progress.
    /// </summary>
    private static IEnumerable<FileRowViewModel> BuildFileRows(TorrentSession session)
    {
        var pieceSize = session.Metadata.PieceSize;
        var completion = session.PieceCompletion;

        foreach (var file in session.Metadata.GetFileInfos())
        {
            if (completion.Length == 0 || pieceSize <= 0 || file.FileSize <= 0)
            {
                yield return new FileRowViewModel(file.Filename, file.FileSize, file.FileSize <= 0 ? 100d : 0d);
                continue;
            }

            var fileEnd = file.FileStartByte + file.FileSize;
            var firstPiece = (int)(file.FileStartByte / pieceSize);
            var lastPiece = (int)Math.Min(completion.Length - 1, (fileEnd - 1) / pieceSize);

            var downloaded = 0L;
            for (var pieceIndex = firstPiece; pieceIndex <= lastPiece; pieceIndex++)
            {
                if (!completion[pieceIndex])
                    continue;

                var pieceStart = (long)pieceIndex * pieceSize;
                var pieceEnd = pieceStart + pieceSize;
                var overlapStart = Math.Max(pieceStart, file.FileStartByte);
                var overlapEnd = Math.Min(pieceEnd, fileEnd);
                downloaded += Math.Max(0, overlapEnd - overlapStart);
            }

            var progressPercent = Math.Round((double)downloaded / file.FileSize * 100, 1);
            yield return new FileRowViewModel(file.Filename, file.FileSize, progressPercent);
        }
    }

    private static IEnumerable<PieceMapCell> BuildPieceMap(bool[] pieceCompletion)
    {
        if (pieceCompletion.Length == 0)
            yield break;

        var cellCount = Math.Min(MaxPieceMapCells, pieceCompletion.Length);
        for (var cell = 0; cell < cellCount; cell++)
        {
            var start = (int)((long)cell * pieceCompletion.Length / cellCount);
            var end = (int)Math.Max(start + 1, (long)(cell + 1) * pieceCompletion.Length / cellCount);

            var have = 0;
            for (var i = start; i < end; i++)
            {
                if (pieceCompletion[i])
                    have++;
            }

            yield return new PieceMapCell((double)have / (end - start));
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}
