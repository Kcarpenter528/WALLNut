using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using WallNut.Service.Api;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;
using WallNut.Shared;

namespace WallNut.Service;

/// <summary>Builds the whole service. Program.cs runs it; tests start it in-process.</summary>
public static class ServiceHost
{
    public const string ServiceName = "WALLNut";

    /// <param name="args">Command-line arguments (they override every settings file).</param>
    /// <param name="machineConfigFile">The machine config file; null = the default location, "" or "none" = don't load one.</param>
    public static WebApplication Build(string[] args, string? machineConfigFile = null, Action<IServiceCollection>? overrides = null)
    {
        // Installed services start in a system folder; settings files live next to the program.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = args.Any(a => a.StartsWith("--contentRoot", StringComparison.OrdinalIgnoreCase)) ? null : AppContext.BaseDirectory,
        });

        var machineConfig = AddMachineConfig(builder.Configuration, machineConfigFile ?? ServicePaths.DefaultConfigFile);
        builder.Services.AddSingleton(machineConfig);

        // Run properly under the Windows Service Control Manager or systemd (both do nothing otherwise).
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddSystemd();

        builder.Services.Configure<WallNutOptions>(builder.Configuration.GetSection(WallNutOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<UpsStateStore>();

        // Event sinks: everything that wants to hear about UPS events.
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<IUpsEventSink, LoggingEventSink>();
        builder.Services.AddSingleton<IUpsEventSink>(sp => sp.GetRequiredService<EventHub>());

        builder.Services.AddHostedService<MonitorService>();

        // Test mode unless WallNut:Shutdown:DryRun is explicitly false.
        var dryRun = builder.Configuration.GetValue("WallNut:Shutdown:DryRun", true);
        if (dryRun) builder.Services.AddSingleton<IShutdownExecutor, DryRunShutdownExecutor>();
        else builder.Services.AddSingleton<IShutdownExecutor, SystemShutdownExecutor>();
        builder.Services.AddSingleton<ShutdownCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ShutdownCoordinator>());

        // Local-only API for the tray and desktop apps (named pipe / Unix socket, no network port).
        var endpoint = builder.Configuration["WallNut:Api:Endpoint"] ?? ServiceEndpoint.Default;
        builder.UseLocalEndpoint(endpoint);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            // Installed on Windows: warnings and errors (power failures, lost contact, shutdowns) go to Event Viewer.
            AddEventLog(builder.Logging);
        }

        overrides?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapLocalApi();
        app.OpenSocketToUsers(endpoint);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            app.Logger.LogInformation("Local API listening on {Endpoint}",
                ServiceEndpoint.IsSocketPath(endpoint) ? endpoint : $@"\\.\pipe\{endpoint}");
            if (machineConfig.Problem is { } problem) app.Logger.LogError("{Problem}", problem);
            else if (machineConfig.Path is { } path)
                app.Logger.LogInformation("Machine settings: {Config} ({State})", path, machineConfig.Loaded ? "loaded" : "not found");
        });
        return app;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(s => { s.SourceName = ServiceName; s.LogName = "Application"; });

    private static bool IsUsable(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the machine config file right after appsettings.*.json, so it overrides the built-in
    /// defaults but environment variables and command-line arguments still override it.
    /// </summary>
    private static MachineConfigStatus AddMachineConfig(ConfigurationManager configuration, string path)
    {
        if (!IsUsable(path)) return new MachineConfigStatus(null, false, null);
        var full = Path.GetFullPath(path);
        var sources = configuration.Sources;
        var lastJson = -1;
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is JsonConfigurationSource j && j.Path?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true) lastJson = i;

        var source = new JsonConfigurationSource { Path = full, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        try
        {
            sources.Insert(lastJson + 1, source); // reads the file now
            return new MachineConfigStatus(full, File.Exists(full), null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or FormatException or InvalidDataException)
        {
            // Run without it and say why, rather than crash: the error lands in Event Viewer / the journal.
            sources.Remove(source);
            var why = e switch
            {
                UnauthorizedAccessException => "access was denied. Administrators and SYSTEM (or root) must be able to read it; re-running the installer repairs this",
                FormatException or InvalidDataException => $"it isn't valid JSON ({e.Message})",
                _ => e.Message,
            };
            return new MachineConfigStatus(full, false, $"Can't read the settings file {full}: {why}.");
        }
    }
}
