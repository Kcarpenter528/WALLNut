using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;

namespace WallNut.Service.Shutdown;

/// <summary>
/// Checks the shutdown rules every second. When too few power supplies are healthy it raises
/// ShutdownInitiated, waits FinalDelaySeconds so alerts can go out, and runs the shutdown.
/// A real shutdown happens once. In test mode it re-arms after things recover, so every
/// rehearsal is reported.
/// </summary>
public sealed class ShutdownCoordinator(
    IOptionsMonitor<WallNutOptions> options,
    UpsStateStore store,
    IEnumerable<IUpsEventSink> sinks,
    IShutdownExecutor executor,
    ILogger<ShutdownCoordinator> log,
    TimeProvider time,
    Actions.IBeforeShutdown? beforeShutdown = null) : BackgroundService
{
    private readonly IReadOnlyList<IUpsEventSink> _sinks = sinks.ToList();
    private bool _initiated;
    private string _lastCritical = "";
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryAfterFailure { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>True once a (real or rehearsed) shutdown has been started and not re-armed.</summary>
    public bool Initiated => _initiated;

    private ShutdownOptions Options => options.CurrentValue.Shutdown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DescribeSetup();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Shutdown check failed"); }

            try { await Task.Delay(CheckInterval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One evaluation. Public so tests can drive it step by step.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        var o = Options;
        var now = time.GetUtcNow();
        var decision = ShutdownPolicy.Evaluate(store.All, o, now);

        var critical = string.Join("; ", decision.Supplies.Where(s => s.Critical).Select(s => $"{s.DisplayName}: {s.Reason}"));
        if (critical != _lastCritical)
        {
            if (critical.Length > 0)
                log.LogWarning("Critical power supply: {Critical} ({Healthy} of {Min} needed supplies healthy)",
                    critical, decision.HealthySupplies, decision.MinimumSupplies);
            else log.LogInformation("No power supply is critical now. {Summary}", decision.Summary);
            _lastCritical = critical;
        }

        if (!decision.ShouldShutDown)
        {
            if (_initiated && o.DryRun)
            {
                _initiated = false;
                await PublishAsync(UpsEventKind.ShutdownRearmed, "Test mode: power is OK again. The next shutdown will be reported too.", ct);
            }
            return;
        }

        if (_initiated || now < _retryAfter) return;
        _initiated = true;

        var delay = TimeSpan.FromSeconds(o.FinalDelaySeconds);
        var initiated = await PublishAsync(UpsEventKind.ShutdownInitiated,
            (o.DryRun ? "TEST MODE - would shut down" : $"{(o.Action == ShutdownAction.Hibernate ? "Hibernating" : "Shutting down")}")
            + $" in {delay.TotalSeconds:0}s. {decision.Summary}", ct);

        // Scripts marked "run before shutdown" run alongside the final delay; the shutdown waits
        // for whichever takes longer (each script is limited by its own time limit).
        var scripts = beforeShutdown?.RunAsync(initiated, ct) ?? Task.CompletedTask;
        var wait = delay > TimeSpan.Zero ? Task.Delay(delay, time, ct) : Task.CompletedTask;
        await Task.WhenAll(scripts, wait);
        try
        {
            await executor.ExecuteAsync(o.Action, decision.Summary, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogCritical(e, "Shutdown command failed");
            _initiated = false;
            _retryAfter = time.GetUtcNow() + RetryAfterFailure;
            await PublishAsync(UpsEventKind.ShutdownFailed,
                $"The shutdown command failed ({e.Message}). Retrying in {RetryAfterFailure.TotalSeconds:0}s.", ct);
        }
    }

    private void DescribeSetup()
    {
        var o = Options;
        var powering = options.CurrentValue.Endpoints
            .Where(e => e.Value.Enabled && e.Value.Role == UpsRole.PowersThisMachine)
            .Select(e => $"{e.Key} (power value {e.Value.PowerValue})")
            .ToList();

        if (!o.Enabled) { log.LogWarning("Shutdown is turned OFF (WallNut:Shutdown:Enabled). This machine will not be shut down."); return; }
        if (powering.Count == 0)
        {
            log.LogWarning("No UPS has Role PowersThisMachine, so WALLNut will never shut this machine down. Alerts still work.");
            return;
        }

        var rules = new List<string>();
        if (o.RespectForcedShutdown) rules.Add("server forces shutdown (FSD)");
        if (o.OnLowBattery) rules.Add("UPS low battery");
        if (o.BatteryChargeBelowPercent is { } c) rules.Add($"charge below {c:0}%");
        if (o.RuntimeBelowSeconds is { } r) rules.Add($"runtime below {r:0}s");
        if (o.OnBatteryForSeconds is { } b) rules.Add($"on battery for {b:0}s");
        rules.Add($"contact lost for {o.LostWhileOnBatterySeconds:0}s while on battery");

        log.Log(o.DryRun ? LogLevel.Warning : LogLevel.Information,
            "Shutdown {Mode}: {Action} when fewer than {Min} of [{Powering}] are healthy. A UPS is critical when: {Rules}.",
            o.DryRun ? "TEST MODE (nothing will actually shut down)" : "ARMED", o.Action, o.MinimumSupplies,
            string.Join(", ", powering), string.Join("; ", rules));
    }

    private async Task<UpsEvent> PublishAsync(UpsEventKind kind, string message, CancellationToken ct)
    {
        var e = new UpsEvent(UpsEvent.ThisMachine, Environment.MachineName, kind, time.GetUtcNow(), null, message);
        foreach (var sink in _sinks)
        {
            try { await sink.PublishAsync(e, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Event sink {Sink} failed for {Kind}", sink.GetType().Name, kind);
            }
        }
        return e;
    }
}
