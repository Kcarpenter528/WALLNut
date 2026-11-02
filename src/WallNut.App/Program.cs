using Avalonia;

namespace WallNut.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One tray app per logged-in user.
        using var single = new Mutex(initiallyOwned: true, @"Local\WALLNut.Tray", out var first);
        if (!first) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
