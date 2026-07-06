using System.Collections.ObjectModel;
using bzTorrentClient.Avalonia.ViewModels;
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

    public ObservableCollection<string> Peers { get; } = new();
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
        Name = string.IsNullOrWhiteSpace(session.Metadata.Name) ? "(fetching metadata…)" : session.Metadata.Name;
        InfoHash = session.Metadata.HashString;
        DownloadDirectory = session.DownloadDirectory;

        ReplaceAll(Trackers, session.Metadata.AnnounceList);
        ReplaceAll(Files, session.Metadata.GetFileInfos().Select(f => new FileRowViewModel(f.Filename, f.FileSize)));

        var peers = _runtimeInfoProvider?.GetConnectedPeers(session.Id).Select(p => p.ToString()) ?? Enumerable.Empty<string>();
        ReplaceAll(Peers, peers);

        ReplaceAll(PieceMap, BuildPieceMap(session.PieceCompletion));
    }

    private void Clear()
    {
        HasSelection = false;
        Name = string.Empty;
        InfoHash = string.Empty;
        DownloadDirectory = string.Empty;
        Trackers.Clear();
        Files.Clear();
        Peers.Clear();
        PieceMap.Clear();
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
