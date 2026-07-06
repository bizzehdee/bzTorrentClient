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

    [ObservableProperty]
    private TorrentRowViewModel? _selectedTorrent;

    [ObservableProperty]
    private string? _lastErrorMessage;

    public event EventHandler<Guid?>? SelectionChanged;

    public TorrentListViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _runtimeInfoProvider = sessionManager as ITorrentRuntimeInfoProvider;
    }

    partial void OnSelectedTorrentChanged(TorrentRowViewModel? value) => SelectionChanged?.Invoke(this, value?.Id);

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
    }
}
