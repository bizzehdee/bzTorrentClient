using bzTorrentClient.Avalonia.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace bzTorrentClient.Avalonia.Features.RemoveTorrent;

public partial class ConfirmRemoveViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _deleteFiles;

    [ObservableProperty]
    private bool _dontAskAgain;

    /// <summary>true = proceed with removal (see <see cref="DeleteFiles"/>/<see cref="DontAskAgain"/>); null = cancelled.</summary>
    public event EventHandler<bool?>? Completed;

    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public ConfirmRemoveViewModel()
    {
        RemoveCommand = new RelayCommand(() => Completed?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => Completed?.Invoke(this, null));
    }
}
