using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WallNutOptions>(builder.Configuration.GetSection(WallNutOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UpsStateStore>();
builder.Services.AddSingleton<IUpsEventSink, LoggingEventSink>();
builder.Services.AddHostedService<MonitorService>();

// Test mode unless WallNut:Shutdown:DryRun is explicitly false.
var dryRun = builder.Configuration.GetValue("WallNut:Shutdown:DryRun", true);
if (dryRun) builder.Services.AddSingleton<IShutdownExecutor, DryRunShutdownExecutor>();
else builder.Services.AddSingleton<IShutdownExecutor, SystemShutdownExecutor>();
builder.Services.AddHostedService<ShutdownCoordinator>();

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Build().Run();
