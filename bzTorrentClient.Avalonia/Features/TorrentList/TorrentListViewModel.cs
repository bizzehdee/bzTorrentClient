using System.Collections.ObjectModel;
using bzTorrentClient.Avalonia.ViewModels;
using bzTorrentClient.Engine.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace bzTorrentClient.Avalonia.Features.TorrentList;

public partial class TorrentListViewModel : ViewModelBase
{
    private readonly ISessionManager _sessionManager;
    private readonly ITorrentRuntimeInfoProvider? _runtimeInfoProvider;

    public ObservableCollection<TorrentRowViewModel> Torrents { get; } = new();

    public IReadOnlyList<TorrentSortMode> SortModes { get; } = Enum.GetValues<TorrentSortMode>();

    [ObservableProperty]
    private TorrentRowViewModel? _selectedTorrent;

    [ObservableProperty]
    private string? _lastErrorMessage;

    [ObservableProperty]
    private TorrentSortMode _sortMode = TorrentSortMode.Default;

    public event EventHandler<Guid?>? SelectionChanged;

    public TorrentListViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _runtimeInfoProvider = sessionManager as ITorrentRuntimeInfoProvider;
    }

    partial void OnSelectedTorrentChanged(TorrentRowViewModel? value) => SelectionChanged?.Invoke(this, value?.Id);

    partial void OnSortModeChanged(TorrentSortMode value) => ApplySort();

    /// <summary>
    /// Pulls the current session list and per-session peer counts and reconciles them
    /// into <see cref="Torrents"/>. Called on a UI timer rather than pushed from the
    /// engine — <see cref="ISessionManager"/> has no change-notification events, and
    /// adding them is a bigger engine change than this UI pass warrants.
    /// </summary>
    public void Refresh()
    {
        var sessions = _sessionManager.Sessions.ToDictionary(s => s.Id);

        for (var i = Torrents.Count - 1; i >= 0; i--)
        {
            if (!sessions.ContainsKey(Torrents[i].Id))
                Torrents.RemoveAt(i);
        }

        foreach (var session in sessions.Values)
        {
            var row = Torrents.FirstOrDefault(r => r.Id == session.Id);
            if (row is null)
            {
                row = new TorrentRowViewModel(session.Id, _sessionManager);
                Torrents.Add(row);
            }

            var stats = _runtimeInfoProvider?.GetNetworkStats(session.Id) ?? TorrentNetworkStats.Empty;
            row.UpdateFrom(session, stats);
        }

        ApplySort();
    }

    /// <summary>
    /// Reorders <see cref="Torrents"/> in place via <see cref="ObservableCollection{T}.Move"/>
    /// rather than clearing and re-adding, so the ListBox's SelectedItem binding (by
    /// reference) survives a re-sort instead of losing the selection.
    /// </summary>
    private void ApplySort()
    {
        IOrderedEnumerable<TorrentRowViewModel> ordered = SortMode switch
        {
            TorrentSortMode.Progress => Torrents.OrderByDescending(t => t.ProgressPercent),
            TorrentSortMode.Size => Torrents.OrderByDescending(t => t.SizeBytes),
            TorrentSortMode.Name => Torrents.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => Torrents.OrderBy(t => t.AddedAtUtc),
        };

        var desired = ordered.ToList();
        for (var i = 0; i < desired.Count; i++)
        {
            var currentIndex = Torrents.IndexOf(desired[i]);
            if (currentIndex != i)
                Torrents.Move(currentIndex, i);
        }
    }
}
