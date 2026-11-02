using WallNut.Service.Api;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;
using WallNut.Shared;

namespace WallNut.Service;

/// <summary>Builds the whole service. Program.cs runs it; tests start it in-process.</summary>
public static class ServiceHost
{
    public static WebApplication Build(string[] args, Action<IServiceCollection>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);

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

        overrides?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapLocalApi();
        app.OpenSocketToUsers(endpoint);
        app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Local API listening on {Endpoint}",
            ServiceEndpoint.IsSocketPath(endpoint) ? endpoint : $@"\\.\pipe\{endpoint}"));
        return app;
    }
}
