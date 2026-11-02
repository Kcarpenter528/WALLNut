using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

/// <summary>A sink that records events so tests can wait for them.</summary>
internal sealed class RecordingSink : IUpsEventSink
{
    public ConcurrentQueue<UpsEvent> Events { get; } = new();

    public Task PublishAsync(UpsEvent e, CancellationToken ct)
    {
        Events.Enqueue(e);
        return Task.CompletedTask;
    }

    public int Count(UpsEventKind kind, string? endpoint = null) =>
        Events.Count(e => e.Kind == kind && (endpoint is null || e.EndpointId == endpoint));

    public async Task<UpsEvent> WaitFor(UpsEventKind kind, string? endpoint = null, int occurrence = 1, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var match = Events.Where(e => e.Kind == kind && (endpoint is null || e.EndpointId == endpoint)).Skip(occurrence - 1).FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(20);
        }
        Assert.Fail($"No {kind} event (#{occurrence}) within {timeoutMs} ms. Got: {string.Join(", ", Events.Select(e => $"{e.EndpointId}:{e.Kind}"))}");
        return null!;
    }
}

internal sealed class ThrowingSink : IUpsEventSink
{
    public Task PublishAsync(UpsEvent e, CancellationToken ct) => throw new InvalidOperationException("webhook down");
}

/// <summary>Monitors running against the simulator over real connections.</summary>
public sealed class UpsMonitorTests : IAsyncLifetime
{
    private readonly List<SimulatorServer> _servers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _running = [];
    private readonly UpsStateStore _store = new();
    private readonly RecordingSink _sink = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_running);
        foreach (var s in _servers) await s.DisposeAsync();
    }

    private SimulatorServer Server(params (string Name, string Profile)[] ups)
    {
        var s = new SimulatorServer(ups.Select(u => new SimulatedUps(u.Name, UpsProfile.Load(u.Profile)) { Jitter = false }),
            new SimulatorOptions { Port = 0 });
        s.StartListening();
        _servers.Add(s);
        return s;
    }

    private static EndpointOptions Endpoint(SimulatorServer s, string ups, string host = "127.0.0.1") => new()
    {
        Host = host,
        Port = s.Port,
        Ups = ups,
        PollIntervalSeconds = 0.1,
        TimeoutSeconds = 3,
    };

    private void Run(string id, EndpointOptions e, params IUpsEventSink[] extraSinks)
    {
        var monitor = new UpsMonitor(id, e,
            new ReconnectOptions { InitialDelaySeconds = 0.1, MaxDelaySeconds = 0.5 },
            _store, [_sink, .. extraSinks], NullLogger.Instance);
        _running.Add(Task.Run(() => monitor.RunAsync(_stop.Token)));
    }

    [Fact]
    public async Task Connects_and_keeps_the_latest_state()
    {
        var s = Server(("rack", "full-featured"));
        Run("rack", Endpoint(s, "rack"));

        var e = await _sink.WaitFor(UpsEventKind.Connected);
        Assert.Contains("rack@127.0.0.1", e.Message);

        var state = _store.Get("rack")!;
        Assert.True(state.Connected);
        Assert.Equal("WNL3K25031400042", state.Var("ups.serial"));
        Assert.True(state.Vars.Count > 300);
        Assert.False(state.Status!.Value.IsOnBattery);
    }

    [Fact]
    public async Task Reports_outage_low_battery_and_power_return()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.Connected);

        s["ups"].PowerFail();
        var onBatt = await _sink.WaitFor(UpsEventKind.OnBattery);
        Assert.Contains("running on battery", onBatt.Message);

        s["ups"].SetCharge(5);
        await _sink.WaitFor(UpsEventKind.LowBattery);

        s["ups"].PowerRestore();
        var back = await _sink.WaitFor(UpsEventKind.OnLine);
        Assert.Contains("Power is back", back.Message);
    }

    [Fact]
    public async Task Starting_mid_outage_reports_it_immediately()
    {
        var s = Server(("ups", "back-ups"));
        s["ups"].PowerFail();
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.OnBattery);
    }

    [Fact]
    public async Task Dropped_connection_is_reported_once_then_restored()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.Connected);

        s["ups"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery);

        s.DropAllClients();
        var lost = await _sink.WaitFor(UpsEventKind.CommunicationLost);
        Assert.Contains("ON BATTERY", lost.Message); // the fail-safe needs to know this
        await _sink.WaitFor(UpsEventKind.CommunicationRestored);
        Assert.True(_store.Get("nas")!.Connected);
    }

    [Fact]
    public async Task Server_down_retries_quietly_until_it_returns()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.Connected);

        s.StopListening();
        s.DropAllClients();
        await _sink.WaitFor(UpsEventKind.CommunicationLost);
        Assert.False(_store.Get("nas")!.Connected);
        Assert.NotNull(_store.Get("nas")!.LastError);

        await Task.Delay(1500); // several retries while down
        Assert.Equal(1, _sink.Count(UpsEventKind.CommunicationLost));

        s.StartListening();
        await _sink.WaitFor(UpsEventKind.CommunicationRestored, timeoutMs: 15000);
    }

    [Fact]
    public async Task Access_denied_explains_the_synology_fix()
    {
        var s = Server(("ups", "back-ups"));
        s.DenyAll = true;
        Run("nas", Endpoint(s, "ups"));

        var lost = await _sink.WaitFor(UpsEventKind.CommunicationLost);
        Assert.Contains("Permitted DiskStation Devices", lost.Message);

        s.DenyAll = false;
        await _sink.WaitFor(UpsEventKind.Connected);
    }

    [Fact]
    public async Task Wrong_ups_name_is_explained()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "UPS"));
        var lost = await _sink.WaitFor(UpsEventKind.CommunicationLost);
        Assert.Contains("no UPS by that name", lost.Message);
    }

    [Fact]
    public async Task Stale_driver_keeps_the_connection_and_recovers()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.Connected);

        s["ups"].SetStale(true);
        await _sink.WaitFor(UpsEventKind.DataStale);
        Assert.True(_store.Get("nas")!.Connected);
        Assert.True(_store.Get("nas")!.Stale);

        s["ups"].SetStale(false);
        await _sink.WaitFor(UpsEventKind.DataRestored);
        Assert.Equal(0, _sink.Count(UpsEventKind.CommunicationLost));
    }

    [Fact]
    public async Task Each_ups_is_independent()
    {
        var a = Server(("ups", "back-ups"));
        var b = Server(("rack", "full-featured"), ("dc", "three-phase"));
        Run("nas", Endpoint(a, "ups"));
        Run("rack", Endpoint(b, "rack"));
        Run("dc", Endpoint(b, "dc"));
        await _sink.WaitFor(UpsEventKind.Connected, "nas");
        await _sink.WaitFor(UpsEventKind.Connected, "rack");
        await _sink.WaitFor(UpsEventKind.Connected, "dc");

        // Two UPSes on the same server still get separate connections.
        Assert.Equal(2, b.ClientCount);

        a.StopListening();
        a.DropAllClients();
        await _sink.WaitFor(UpsEventKind.CommunicationLost, "nas");

        b["dc"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery, "dc");
        Assert.Equal(0, _sink.Count(UpsEventKind.CommunicationLost, "rack"));
        Assert.Equal(0, _sink.Count(UpsEventKind.OnBattery, "rack"));
        Assert.True(_store.Get("rack")!.Connected);
    }

    [Fact]
    public async Task Host_can_be_a_dns_name()
    {
        var s = Server(("ups", "back-ups"));
        var e = Endpoint(s, "ups", host: "localhost");
        e.TimeoutSeconds = 10;
        Run("nas", e);
        var connected = await _sink.WaitFor(UpsEventKind.Connected, timeoutMs: 15000);
        Assert.Contains("ups@localhost", connected.Message);
    }

    [Fact]
    public async Task Unknown_host_name_is_explained()
    {
        // ".invalid" is reserved and never resolves.
        Run("nas", new EndpointOptions { Host = "no-such-nas.invalid", Ups = "ups", TimeoutSeconds = 10 });
        var lost = await _sink.WaitFor(UpsEventKind.CommunicationLost, timeoutMs: 15000);
        Assert.Contains("couldn't find that host name", lost.Message);
    }

    [Fact]
    public async Task Powers_this_machine_logs_in_like_upsmon()
    {
        var s = Server(("ups", "back-ups"));
        var e = Endpoint(s, "ups");
        e.Role = UpsRole.PowersThisMachine;
        e.Username = "monuser";
        e.Password = "secret";
        Run("nas", e);
        await _sink.WaitFor(UpsEventKind.Connected);
        Assert.Contains(s.Clients(), c => c.LoggedInTo == "ups" && c.User == "monuser");
    }

    [Fact]
    public async Task Wrong_password_is_explained()
    {
        var s = Server(("ups", "back-ups"));
        var e = Endpoint(s, "ups");
        e.Role = UpsRole.PowersThisMachine;
        e.Username = "monuser";
        e.Password = "nope";
        Run("nas", e);
        var lost = await _sink.WaitFor(UpsEventKind.CommunicationLost);
        Assert.Contains("refused access", lost.Message);
    }

    [Fact]
    public async Task A_failing_sink_does_not_stop_monitoring()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"), new ThrowingSink());
        await _sink.WaitFor(UpsEventKind.Connected);
        s["ups"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery);
    }

    [Fact]
    public async Task Stopping_logs_out_cleanly()
    {
        var s = Server(("ups", "back-ups"));
        Run("nas", Endpoint(s, "ups"));
        await _sink.WaitFor(UpsEventKind.Connected);
        Assert.Equal(1, s.ClientCount);

        _stop.Cancel();
        await Task.WhenAll(_running);
        var sw = Stopwatch.StartNew();
        while (s.ClientCount > 0 && sw.ElapsedMilliseconds < 3000) await Task.Delay(20);
        Assert.Equal(0, s.ClientCount);
    }
}
