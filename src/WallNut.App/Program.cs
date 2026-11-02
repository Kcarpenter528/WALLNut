using Avalonia;

namespace WallNut.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One tray app per logged-in user and service (a development copy can run next to the installed one).
        var endpoint = WallNut.Shared.ServiceEndpoint.Default;
        var name = endpoint == WallNut.Shared.ServiceEndpoint.DefaultPipeName ? "WALLNut.Tray"
            : "WALLNut.Tray." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint)))[..12];
        using var single = new Mutex(initiallyOwned: true, @"Local\" + name, out var first);
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
