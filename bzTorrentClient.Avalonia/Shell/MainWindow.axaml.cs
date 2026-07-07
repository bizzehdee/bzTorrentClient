using Avalonia.Controls;
using bzTorrentClient.Avalonia.Features.AddTorrent;
using bzTorrentClient.Avalonia.Features.RemoveTorrent;
using bzTorrentClient.Avalonia.Features.Settings;

namespace bzTorrentClient.Avalonia.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        viewModel.AddTorrentRequested += (_, _) =>
        {
            var window = new AddTorrentWindow
            {
                DataContext = new AddTorrentViewModel(viewModel.AddPipeline, viewModel.Settings),
            };
            _ = window.ShowDialog(this);
        };

        viewModel.SettingsRequested += (_, _) =>
        {
            var window = new SettingsWindow
            {
                DataContext = new SettingsViewModel(viewModel.Settings, viewModel.SettingsStore),
            };
            _ = window.ShowDialog(this);
        };

        viewModel.ConfirmRemoveRequested += (_, sessionId) =>
        {
            var remembered = viewModel.Settings.RememberedDeleteFilesOnRemove;
            if (remembered.HasValue)
            {
                _ = viewModel.RemoveTorrentAsync(sessionId, remembered.Value);
                return;
            }

            var confirmViewModel = new ConfirmRemoveViewModel();
            var window = new ConfirmRemoveWindow { DataContext = confirmViewModel };

            confirmViewModel.Completed += (_, proceed) =>
            {
                if (proceed != true)
                    return;

                if (confirmViewModel.DontAskAgain)
                {
                    viewModel.Settings.RememberedDeleteFilesOnRemove = confirmViewModel.DeleteFiles;
                    viewModel.SettingsStore.Save(viewModel.Settings);
                }

                _ = viewModel.RemoveTorrentAsync(sessionId, confirmViewModel.DeleteFiles);
            };

            _ = window.ShowDialog(this);
        };
    }
}
