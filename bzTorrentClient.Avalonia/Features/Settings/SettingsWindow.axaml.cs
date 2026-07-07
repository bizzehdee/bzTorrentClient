using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace bzTorrentClient.Avalonia.Features.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.Saved += (_, _) => Close();
    }

    private async void OnBrowseDownloadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the default download directory",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && DataContext is SettingsViewModel viewModel)
            viewModel.DefaultDownloadDirectory = folders[0].Path.LocalPath;
    }

    private async void OnBrowseLogDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the log directory",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && DataContext is SettingsViewModel viewModel)
            viewModel.LogDirectory = folders[0].Path.LocalPath;
    }

    private async void OnBrowseIpBlocklistFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an IP blocklist file",
            AllowMultiple = false,
        });

        if (files.Count > 0 && DataContext is SettingsViewModel viewModel)
            viewModel.IpBlocklistFilePath = files[0].Path.LocalPath;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
