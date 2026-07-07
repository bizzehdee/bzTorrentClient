using Avalonia;
using System;

namespace bzTorrentClient.Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia's X11 input-method (IME) bridge to ibus/fcitx can hang for several
            // seconds the first time a text box is focused on some Linux setups - notably
            // Wayland-via-XWayland with ibus running - repeatedly churning the input-method
            // context. Every text field in this app takes plain ASCII (file paths, magnet
            // links, hex info-hashes), so the IME buys nothing here; turning it off avoids
            // the focus stall entirely.
            .With(new X11PlatformOptions { EnableIme = false })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
