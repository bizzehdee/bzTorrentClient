using Avalonia.Controls;
using Avalonia.Interactivity;

namespace bzTorrentClient.Avalonia.Features.About;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
