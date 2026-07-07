using bzTorrentClient.Avalonia.Features.RemoveTorrent;

namespace bzTorrentClient.Avalonia.Tests.Features.RemoveTorrent;

public class ConfirmRemoveViewModelTests
{
    [Fact]
    public void RemoveCommand_RaisesCompletedWithTrue()
    {
        var viewModel = new ConfirmRemoveViewModel();
        bool? result = false;
        viewModel.Completed += (_, proceed) => result = proceed;

        viewModel.RemoveCommand.Execute(null);

        Assert.True(result);
    }

    [Fact]
    public void CancelCommand_RaisesCompletedWithNull()
    {
        var viewModel = new ConfirmRemoveViewModel();
        var invoked = false;
        bool? result = true;
        viewModel.Completed += (_, proceed) =>
        {
            invoked = true;
            result = proceed;
        };

        viewModel.CancelCommand.Execute(null);

        Assert.True(invoked);
        Assert.Null(result);
    }

    [Fact]
    public void DeleteFilesAndDontAskAgain_DefaultToFalse()
    {
        var viewModel = new ConfirmRemoveViewModel();

        Assert.False(viewModel.DeleteFiles);
        Assert.False(viewModel.DontAskAgain);
    }
}
