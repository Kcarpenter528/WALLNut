using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using WallNut.History;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Shared;

namespace WallNut.Service.HistoryRecording;

/// <summary>Opens the history database (or explains why it couldn't), shared by the recorder and the API.</summary>
public sealed class HistoryDatabase : IDisposable
{
    public HistoryStore? Store { get; }
    public string? Problem { get; }

    public HistoryDatabase(IOptions<WallNutOptions> options, ILogger<HistoryDatabase> log)
    {
        var o = options.Value.History;
        if (!o.Enabled) { Problem = "History is turned off (WallNut:History:Enabled)."; return; }

        var retention = new HistoryRetention(o.RawDays, o.FiveMinuteDays, o.HourlyDays, o.EventDays);
        var path = o.DatabasePath ?? Path.Combine(ServicePaths.DataDirectory, "history.db");
        try
        {
            Store = new HistoryStore(path, retention, o.SampleIntervalSeconds);
        }
        catch (Exception e) when (o.DatabasePath is null && e is UnauthorizedAccessException or SqliteException or IOException)
        {
            // A development run can't write to the admin-only data folder; use this user's folder instead.
            var fallback = Path.Combine(ServicePaths.UserDataDirectory, "history-dev.db");
            log.LogWarning("Can't write history to {Path} ({Reason}); using {Fallback}", path, e.Message, fallback);
            Store = new HistoryStore(fallback, retention, o.SampleIntervalSeconds);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SqliteException or IOException)
        {
            Problem = $"Can't open the history database {path}: {e.Message}";
            log.LogError("{Problem}", Problem);
        }
        if (Store is not null) log.LogInformation("Recording history to {Path}", Store.Path);
    }

    public void Dispose() => Store?.Dispose();
}

/// <summary>Saves every event, so outages can be worked out later.</summary>
public sealed class HistoryEventSink(HistoryDatabase db, ILogger<HistoryEventSink> log) : IUpsEventSink
{
    public Task PublishAsync(UpsEvent e, CancellationToken ct)
    {
        try
        {
            db.Store?.AddEvent(new ServiceEvent(0, e.EndpointId, e.DisplayName, e.Kind.ToString(), e.Time, e.Status?.Raw, e.Message, e.IsProblem));
        }
        catch (SqliteException ex) { log.LogWarning(ex, "Couldn't save {Kind} to history", e.Kind); }
        return Task.CompletedTask;
    }
}

/// <summary>Samples every connected UPS on a timer, and condenses and trims history every few minutes.</summary>
public sealed class HistoryRecorder(
    HistoryDatabase db, UpsStateStore states, IOptions<WallNutOptions> options, TimeProvider time, ILogger<HistoryRecorder> log)
    : BackgroundService
{
    private static readonly TimeSpan MaintenanceEvery = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (db.Store is not { } store) return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.History.SampleIntervalSeconds));
        var nextMaintenance = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(interval, time);
        do
        {
            var now = time.GetUtcNow();
            try
            {
                SampleOnce(store, now);
                if (now >= nextMaintenance)
                {
                    store.Rollup(now);
                    store.ApplyRetention(now);
                    nextMaintenance = now + MaintenanceEvery;
                }
            }
            catch (SqliteException e) { log.LogWarning(e, "History write failed; will try again"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Records each UPS that has fresh data. Disconnected or stale ones leave a gap, which charts show as one.</summary>
    public void SampleOnce(HistoryStore store, DateTimeOffset now)
    {
        foreach (var s in states.All)
        {
            if (!s.Connected || s.Stale || s.LastUpdate is not { } updated) continue;
            if (now - updated > TimeSpan.FromSeconds(Math.Max(30, options.Value.History.SampleIntervalSeconds * 2))) continue;
            store.AddSamples(s.EndpointId, s.DisplayName, now, MetricExtractor.Extract(s.Vars));
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
