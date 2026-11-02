using Microsoft.Extensions.Logging;

namespace WallNut.Service.Monitoring;

/// <summary>Receives UPS events. Logging, notifications, webhooks and history each get one.</summary>
public interface IUpsEventSink
{
    Task PublishAsync(UpsEvent e, CancellationToken ct);
}

/// <summary>Writes every event to the log: warnings for problems, information otherwise.</summary>
public sealed class LoggingEventSink(ILogger<LoggingEventSink> log) : IUpsEventSink
{
    public Task PublishAsync(UpsEvent e, CancellationToken ct)
    {
        log.Log(e.IsProblem ? LogLevel.Warning : LogLevel.Information,
            "[{Endpoint}] {Kind}: {Message}", e.DisplayName, e.Kind, e.Message);
        return Task.CompletedTask;
    }
}
