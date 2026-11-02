using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WallNutOptions>(builder.Configuration.GetSection(WallNutOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UpsStateStore>();
builder.Services.AddSingleton<IUpsEventSink, LoggingEventSink>();
builder.Services.AddHostedService<MonitorService>();

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Build().Run();
