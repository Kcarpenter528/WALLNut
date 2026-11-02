using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;

namespace WallNut.Service.Monitoring;

/// <summary>Starts one <see cref="UpsMonitor"/> per enabled endpoint and keeps them running.</summary>
public sealed class MonitorService(
    IOptions<WallNutOptions> options,
    UpsStateStore store,
    IEnumerable<IUpsEventSink> sinks,
    ILoggerFactory loggers,
    TimeProvider time) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<MonitorService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        var problems = config.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) _log.LogError("Configuration problem: {Problem}", p);
            _log.LogError("Not monitoring anything until the configuration is fixed.");
            return;
        }

        var sinkList = sinks.ToList();
        var monitors = config.Endpoints
            .Where(e => e.Value.Enabled)
            .Select(e => new UpsMonitor(e.Key, e.Value, config.Reconnect, store, sinkList,
                loggers.CreateLogger($"WallNut.Monitor.{e.Key}"), time))
            .ToList();

        foreach (var (id, e) in config.Endpoints.Where(e => e.Value.Enabled))
            _log.LogInformation("Monitoring {Id}: {Ups}@{Host}:{Port} ({Role}, every {Poll}s)", id, e.Ups, e.Host, e.Port, e.Role, e.PollIntervalSeconds);

        // Each monitor is independent: one server being down never affects the others.
        await Task.WhenAll(monitors.Select(m => m.RunAsync(stoppingToken)));
    }
}
