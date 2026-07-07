using Avalonia.Controls;

namespace bzTorrentClient.Avalonia.Features.RemoveTorrent;

public partial class ConfirmRemoveWindow : Window
{
    public ConfirmRemoveWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ConfirmRemoveViewModel viewModel)
            viewModel.Completed += (_, _) => Close();
    }
}
