using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace bzTorrentClient.Avalonia.Features.AddTorrent;

public partial class AddTorrentWindow : Window
{
    public AddTorrentWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is AddTorrentViewModel viewModel)
            viewModel.Completed += (_, _) => Close();
    }

    private async void OnBrowseTorrentFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a .torrent file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Torrent files") { Patterns = new[] { "*.torrent" } } },
        });

        if (files.Count > 0 && DataContext is AddTorrentViewModel viewModel)
            viewModel.TorrentFilePath = files[0].Path.LocalPath;
    }

    private async void OnBrowseDownloadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a download directory",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && DataContext is AddTorrentViewModel viewModel)
            viewModel.DownloadDirectory = folders[0].Path.LocalPath;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
