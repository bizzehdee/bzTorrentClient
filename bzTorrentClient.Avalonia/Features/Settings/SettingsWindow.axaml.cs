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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
