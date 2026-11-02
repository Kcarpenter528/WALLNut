using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using WallNut.Service.Api;
using WallNut.Shared;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

/// <summary>The real service, started in-process on its own pipe, talked to through the real client.</summary>
public sealed class LocalApiTests : IAsyncLifetime
{
    private readonly string _endpoint = OperatingSystem.IsWindows()
        ? $"WallNut.Test.{Guid.NewGuid():N}"
        : Path.Combine(Path.GetTempPath(), $"wallnut-test-{Guid.NewGuid():N}.sock");
    private SimulatorServer _sim = null!;
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        _sim = new SimulatorServer([new SimulatedUps("ups", UpsProfile.Load("back-ups")) { Jitter = false }],
            new SimulatorOptions { Port = 0 });
        _sim.StartListening();

        _app = Service.ServiceHost.Build(
        [
            "--environment", "Testing",
            $"--WallNut:Api:Endpoint={_endpoint}",
            "--WallNut:Endpoints:nas:Enabled=false", // never touch a real NAS from tests
            "--WallNut:Endpoints:sim:Host=127.0.0.1",
            $"--WallNut:Endpoints:sim:Port={_sim.Port}",
            "--WallNut:Endpoints:sim:Ups=ups",
            "--WallNut:Endpoints:sim:DisplayName=Test UPS",
            "--WallNut:Endpoints:sim:PollIntervalSeconds=0.1",
            "--Logging:LogLevel:Default=Warning",
        ]);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _sim.DisposeAsync();
    }

    private WallNutServiceClient Client() => new(_endpoint);

    private static async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = await get();
            if (ok(v)) return v;
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Condition not met; last value: {v}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Info_reports_the_ups_and_shutdown_mode()
    {
        using var c = Client();
        var info = await Eventually(() => c.GetInfoAsync(), i => i.Ups.Any(u => u.Connected));

        Assert.Equal(Environment.MachineName, info.Machine);
        Assert.True(info.ShutdownTestMode);
        var ups = Assert.Single(info.Ups);
        Assert.Equal("Test UPS", ups.DisplayName);
        Assert.Equal("Back-UPS ES 600M1", ups.Model);
        Assert.Equal("5B2318T47115", ups.Serial);
        Assert.Equal(100, ups.ChargePercent);
        Assert.False(ups.OnBattery);
    }

    [Fact]
    public async Task Live_stream_delivers_new_events_to_every_app()
    {
        using var a = Client();
        using var b = Client();
        await Eventually(() => a.GetInfoAsync(), i => i.Ups.Any(u => u.Connected));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gotA = FirstOfKind(a, "OnBattery", cts.Token);
        var gotB = FirstOfKind(b, "OnBattery", cts.Token);
        await WaitForSubscribers(2);

        _sim["ups"].PowerFail();

        var ea = await gotA;
        var eb = await gotB;
        Assert.Equal("sim", ea.EndpointId);
        Assert.Equal("Test UPS", ea.DisplayName);
        Assert.True(ea.IsProblem);
        Assert.Contains("running on battery", ea.Message);
        Assert.Equal(ea.Sequence, eb.Sequence);
    }

    [Fact]
    public async Task Test_notification_goes_through_the_service()
    {
        using var c = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = FirstOfKind(c, "Test", cts.Token);
        await WaitForSubscribers(1);

        await c.SendTestAsync();
        var e = await got;
        Assert.Equal(Environment.MachineName, e.DisplayName);
        Assert.Contains("notifications are working", e.Message);

        Assert.Contains(await c.GetRecentEventsAsync(), r => r.Kind == "Test");
    }

    [Fact]
    public async Task Streaming_does_not_replay_old_events()
    {
        using var c = Client();
        await Eventually(() => c.GetRecentEventsAsync(), r => r.Any(e => e.Kind == "Connected"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = FirstOfKind(c, null, cts.Token);
        await WaitForSubscribers(1);
        await c.SendTestAsync();
        Assert.Equal("Test", (await first).Kind); // not the earlier "Connected"
    }

    [Fact]
    public void Every_service_event_kind_is_in_the_notification_catalog()
    {
        var known = NotificationCatalog.All.Select(k => k.Kind).ToHashSet();
        var missing = Enum.GetNames<Monitoring.UpsEventKind>().Where(k => !known.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Not in NotificationCatalog: " + string.Join(", ", missing));
    }

    private async Task WaitForSubscribers(int n)
    {
        var hub = (EventHub)_app.Services.GetService(typeof(EventHub))!;
        var sw = Stopwatch.StartNew();
        while (hub.SubscriberCount < n && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.Equal(n, hub.SubscriberCount);
    }

    private static Task<ServiceEvent> FirstOfKind(WallNutServiceClient c, string? kind, CancellationToken ct) => Task.Run(async () =>
    {
        await foreach (var e in c.StreamEventsAsync(ct))
            if (kind is null || e.Kind == kind) return e;
        throw new InvalidOperationException("Stream ended");
    }, ct);
}
