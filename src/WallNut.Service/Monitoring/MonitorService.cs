using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;

namespace WallNut.Service.Monitoring;

/// <summary>
/// Runs one <see cref="UpsMonitor"/> per enabled endpoint and keeps the set in step with the
/// settings: when the settings file changes, new UPSes start, removed ones stop, changed ones
/// restart, and untouched ones keep their connection. A broken change is refused and the last
/// good set keeps running.
/// </summary>
public sealed class MonitorService(
    IOptionsMonitor<WallNutOptions> options,
    UpsStateStore store,
    IEnumerable<IUpsEventSink> sinks,
    ILoggerFactory loggers,
    TimeProvider time) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<MonitorService>();
    private readonly List<IUpsEventSink> _sinks = sinks.ToList();
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Running(string Fingerprint, CancellationTokenSource Stop, Task Task);

    /// <summary>Ids being monitored right now (for tests and diagnostics).</summary>
    public IReadOnlyCollection<string> Monitoring { get { lock (_running) return _running.Keys.ToList(); } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = options.OnChange(changed => { _ = ApplyAsync(stoppingToken, reload: true); });
        await ApplyAsync(stoppingToken, reload: false);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        List<Running> all;
        lock (_running) all = _running.Values.ToList();
        foreach (var r in all) r.Stop.Cancel();
        await Task.WhenAll(all.Select(r => r.Task));
    }

    private async Task ApplyAsync(CancellationToken stoppingToken, bool reload)
    {
        if (stoppingToken.IsCancellationRequested) return;
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            // A save often raises several change notifications; let them settle.
            if (reload) await Task.Delay(TimeSpan.FromMilliseconds(300), time, stoppingToken);

            var config = options.CurrentValue;
            var problems = config.Validate();
            if (problems.Count > 0)
            {
                foreach (var p in problems) _log.LogError("Configuration problem: {Problem}", p);
                _log.LogError(reload ? "Settings change ignored; still monitoring with the previous settings."
                                     : "Not monitoring anything until the configuration is fixed.");
                return;
            }
            if (config.HasNoUps) _log.LogWarning("{Note}", WallNutOptions.NoUpsNote);

            var wanted = config.Endpoints
                .Where(e => e.Value.Enabled)
                .ToDictionary(e => e.Key, e => (Options: e.Value, Fingerprint: Fingerprint(e.Value, config.Reconnect)), StringComparer.OrdinalIgnoreCase);

            // Stop what was removed or changed.
            List<(string Id, Running Run)> stopping;
            lock (_running)
                stopping = _running.Where(r => !wanted.TryGetValue(r.Key, out var w) || w.Fingerprint != r.Value.Fingerprint)
                    .Select(r => (r.Key, r.Value)).ToList();
            foreach (var (id, run) in stopping)
            {
                run.Stop.Cancel();
                try { await run.Task; } catch (OperationCanceledException) { }
                lock (_running) _running.Remove(id);
                if (!wanted.ContainsKey(id)) { store.Remove(id); _log.LogInformation("Stopped monitoring {Id}", id); }
            }

            // Start what's new (or changed).
            foreach (var (id, w) in wanted)
            {
                lock (_running) if (_running.ContainsKey(id)) continue;
                var e = w.Options;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var monitor = new UpsMonitor(id, e, config.Reconnect, store, _sinks, loggers.CreateLogger($"WallNut.Monitor.{id}"), time);
                var task = Task.Run(() => monitor.RunAsync(stop.Token), CancellationToken.None);
                lock (_running) _running[id] = new Running(w.Fingerprint, stop, task);
                _log.LogInformation("Monitoring {Id}: {Ups}@{Host}:{Port} ({Role}, every {Poll}s)", id, e.Ups, e.Host, e.Port, e.Role, e.PollIntervalSeconds);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _gate.Release(); }
    }

    private static string Fingerprint(EndpointOptions e, ReconnectOptions r) => JsonSerializer.Serialize(new { e, r });
}
