using bzTorrentClient.Avalonia.Features.TorrentList;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Avalonia.Tests.Features.TorrentList;

public class TorrentRowViewModelTests
{
    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Theory]
    [InlineData(TorrentState.Paused, true, false, true)]
    [InlineData(TorrentState.Stopped, true, false, false)]
    [InlineData(TorrentState.Error, true, false, true)]
    [InlineData(TorrentState.Completed, true, false, true)]
    [InlineData(TorrentState.Active, false, true, true)]
    [InlineData(TorrentState.Seeding, false, true, true)]
    [InlineData(TorrentState.Checking, false, false, true)]
    public async Task CanExecute_MatchesValidStateTransitions(TorrentState state, bool canStart, bool canPause, bool canStop)
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var row = new TorrentRowViewModel(session.Id, sessionManager);

        row.State = state;

        Assert.Equal(canStart, row.StartCommand.CanExecute(null));
        Assert.Equal(canPause, row.PauseCommand.CanExecute(null));
        Assert.Equal(canStop, row.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StateChange_RaisesCanExecuteChangedForAllThreeCommands()
    {
        var sessionManager = new FakeSessionManager();
        var session = await sessionManager.AddAsync(Source(), "/downloads", false);
        var row = new TorrentRowViewModel(session.Id, sessionManager) { State = TorrentState.Paused };

        var raisedCommands = new HashSet<string>();
        row.StartCommand.CanExecuteChanged += (_, _) => raisedCommands.Add(nameof(row.StartCommand));
        row.PauseCommand.CanExecuteChanged += (_, _) => raisedCommands.Add(nameof(row.PauseCommand));
        row.StopCommand.CanExecuteChanged += (_, _) => raisedCommands.Add(nameof(row.StopCommand));

        row.State = TorrentState.Active;

        Assert.Equal(3, raisedCommands.Count);
    }
}
