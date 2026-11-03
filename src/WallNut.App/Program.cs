using Avalonia;

namespace WallNut.App;

internal static class Program
{
    // WALLNut                     tray app (shows the main window on demand)
    // WALLNut --open              ... and open the main window now
    // WALLNut --history [--range=30d] / --settings
    // WALLNut --admin-settings    settings only, for changing them as an administrator
    // WALLNut --endpoint=NAME     talk to a service on a different pipe/socket (development)
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--endpoint=", StringComparison.Ordinal)) is { } endpoint)
            Environment.SetEnvironmentVariable(WallNut.Shared.ServiceEndpoint.OverrideVariable, endpoint["--endpoint=".Length..]);

        // The elevated settings copy runs alongside the user's tray app.
        if (args.Contains("--admin-settings")) return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // One tray app per logged-in user and service (a development copy can run next to the installed one).
        var service = WallNut.Shared.ServiceEndpoint.Default;
        var name = service == WallNut.Shared.ServiceEndpoint.DefaultPipeName ? "WALLNut.Tray"
            : "WALLNut.Tray." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(service)))[..12];
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
            .LogToTextWriter(Console.Error, Avalonia.Logging.LogEventLevel.Warning)
#else
            .LogToTrace()
#endif
            .WithInterFont();
}
