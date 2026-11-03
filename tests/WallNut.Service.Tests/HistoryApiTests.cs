using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using WallNut.Shared;
using WallNut.Shared.History;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

/// <summary>The service recording a simulated UPS, read back through the real client.</summary>
public sealed class HistoryApiTests : IAsyncLifetime
{
    private readonly string _endpoint = TestEndpoint.New();
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"wallnut-history-api-{Guid.NewGuid():N}.db");
    private SimulatorServer _sim = null!;
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        _sim = new SimulatorServer([new SimulatedUps("ups", UpsProfile.Load("back-ups")) { Jitter = false }],
            new SimulatorOptions { Port = 0 });
        _sim.StartListening();
        _app = ServiceHost.Build(
        [
            "--environment", "Testing",
            $"--WallNut:Api:Endpoint={_endpoint}",
            "--WallNut:Endpoints:sim:Host=127.0.0.1",
            $"--WallNut:Endpoints:sim:Port={_sim.Port}",
            "--WallNut:Endpoints:sim:Ups=ups",
            "--WallNut:Endpoints:sim:DisplayName=Test UPS",
            "--WallNut:Endpoints:sim:PollIntervalSeconds=0.1",
            $"--WallNut:History:DatabasePath={_db}",
            "--WallNut:History:SampleIntervalSeconds=1",
            "--Logging:LogLevel:Default=Warning",
        ], machineConfigFile: "none");
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _sim.DisposeAsync();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = await get();
            if (ok(v)) return v;
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("Condition not met in time.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Records_readings_events_and_outages()
    {
        using var c = new WallNutServiceClient(_endpoint);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);

        // A couple of samples on line power, then an outage, then power back.
        await Eventually(() => c.GetHistoryMetricsAsync("sim"), m => m.Any(x => x.Metric == "battery.charge"));
        _sim["ups"].PowerFail();
        await Task.Delay(2500);
        _sim["ups"].SetCharge(80);
        await Task.Delay(1500);
        _sim["ups"].PowerRestore();
        await Eventually(() => c.GetOutagesAsync("sim", start, DateTimeOffset.UtcNow.AddMinutes(1)), s => s.Outages.Any(o => o.End is not null));

        var endpoints = await c.GetHistoryEndpointsAsync();
        Assert.Contains(endpoints, e => e.EndpointId == "sim" && e.DisplayName == "Test UPS");

        var metrics = (await c.GetHistoryMetricsAsync("sim")).Select(m => m.Metric).ToList();
        Assert.Contains("battery.charge", metrics);
        Assert.Contains("battery.runtime", metrics);
        Assert.Contains("input.voltage", metrics);
        Assert.Contains(MetricCatalog.OnBattery, metrics);
        Assert.DoesNotContain("battery.charge.low", metrics);

        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var history = await c.GetHistoryAsync("sim", start, now, ["battery.charge", MetricCatalog.OnBattery]);
        var charge = history.Series.Single(s => s.Metric == "battery.charge");
        Assert.True(charge.Points.Count >= 4, $"only {charge.Points.Count} points");
        Assert.Equal("%", charge.Unit);
        Assert.Contains(charge.Points, p => p.Min <= 80);
        Assert.Contains(history.Series.Single(s => s.Metric == MetricCatalog.OnBattery).Points, p => p.Max == 1);

        var outage = (await c.GetOutagesAsync("sim", start, now)).Outages.Single();
        Assert.Equal("Test UPS", outage.DisplayName);
        Assert.InRange(outage.DurationSeconds, 3, 15);
        Assert.InRange(outage.LowestCharge!.Value, 79, 81);
        Assert.Equal("Power restored", outage.EndReason);

        var events = await c.GetHistoryEventsAsync("sim", start, now);
        Assert.Equal(["OnLine", "OnBattery"], events.Where(e => e.Kind is "OnLine" or "OnBattery").Select(e => e.Kind));

        using var csv = new MemoryStream();
        await c.ExportCsvAsync("sim", start, now, csv);
        var text = System.Text.Encoding.UTF8.GetString(csv.ToArray());
        Assert.StartsWith("time_utc,", text);
        Assert.Contains("battery.charge (%)", text.Split('\n')[0]);
    }

    [Fact]
    public async Task Nothing_recorded_while_disconnected()
    {
        using var c = new WallNutServiceClient(_endpoint);
        await Eventually(() => c.GetHistoryMetricsAsync("sim"), m => m.Any());

        _sim.StopListening();
        _sim.DropAllClients();
        await Task.Delay(500);
        var cut = DateTimeOffset.UtcNow;
        await Task.Delay(3000);

        var after = await c.GetHistoryAsync("sim", cut.AddSeconds(1), DateTimeOffset.UtcNow, ["battery.charge"]);
        Assert.Empty(after.Series[0].Points); // a gap, not made-up values
    }
}
