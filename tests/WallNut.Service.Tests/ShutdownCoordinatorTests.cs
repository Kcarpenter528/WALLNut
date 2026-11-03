using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

internal sealed class RecordingExecutor : IShutdownExecutor
{
    public ConcurrentQueue<(ShutdownAction Action, string Reason)> Calls { get; } = new();
    public int FailNext { get; set; }

    public Task ExecuteAsync(ShutdownAction action, string reason, CancellationToken ct)
    {
        if (FailNext > 0) { FailNext--; throw new InvalidOperationException("access denied"); }
        Calls.Enqueue((action, reason));
        return Task.CompletedTask;
    }
}

public class ShutdownCoordinatorTests
{
    private readonly UpsStateStore _store = new();
    private readonly RecordingSink _sink = new();
    private readonly RecordingExecutor _exec = new();

    private ShutdownCoordinator Make(ShutdownOptions shutdown) => new(
        TestOptions.Fixed(new WallNutOptions { Shutdown = shutdown }), _store, [_sink], _exec,
        NullLogger<ShutdownCoordinator>.Instance, TimeProvider.System)
    { RetryAfterFailure = TimeSpan.Zero };

    private static ShutdownOptions Opts(bool dryRun = false) => new() { DryRun = dryRun, FinalDelaySeconds = 0 };

    [Fact]
    public async Task Healthy_power_does_nothing()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB DISCHRG", charge: 90));
        var c = Make(Opts());
        await c.CheckAsync(default);
        Assert.Empty(_exec.Calls);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task Critical_power_announces_then_shuts_down_once()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        var c = Make(Opts());

        await c.CheckAsync(default);
        await c.CheckAsync(default);
        await c.CheckAsync(default);

        var call = Assert.Single(_exec.Calls);
        Assert.Equal(ShutdownAction.Shutdown, call.Action);
        Assert.Contains("low battery", call.Reason);

        var e = Assert.Single(_sink.Events);
        Assert.Equal(UpsEventKind.ShutdownInitiated, e.Kind);
        Assert.Equal(UpsEvent.ThisMachine, e.EndpointId);
        Assert.StartsWith("Shutting down in 0s", e.Message);
    }

    [Fact]
    public async Task Real_shutdown_stays_latched_even_if_power_returns()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        var c = Make(Opts());
        await c.CheckAsync(default);

        _store.Set(ShutdownPolicyTests.Ups("nas", "OL"));
        await c.CheckAsync(default);
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        await c.CheckAsync(default);

        Assert.Single(_exec.Calls);
        Assert.True(c.Initiated);
    }

    [Fact]
    public async Task Test_mode_rearms_so_every_rehearsal_is_reported()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        var c = Make(Opts(dryRun: true));
        await c.CheckAsync(default);
        Assert.StartsWith("TEST MODE", _sink.Events.Single().Message);

        _store.Set(ShutdownPolicyTests.Ups("nas", "OL"));
        await c.CheckAsync(default);
        Assert.False(c.Initiated);
        Assert.Contains(_sink.Events, e => e.Kind == UpsEventKind.ShutdownRearmed);

        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        await c.CheckAsync(default);
        Assert.Equal(2, _exec.Calls.Count);
    }

    [Fact]
    public async Task Failed_shutdown_is_reported_and_retried()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        _exec.FailNext = 1;
        var c = Make(Opts());

        await c.CheckAsync(default);
        Assert.Contains(_sink.Events, e => e.Kind == UpsEventKind.ShutdownFailed && e.Message.Contains("access denied"));
        Assert.Empty(_exec.Calls);

        await c.CheckAsync(default);
        Assert.Single(_exec.Calls);
    }

    [Fact]
    public async Task Hibernate_action_is_passed_through()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OL FSD"));
        var o = Opts();
        o.Action = ShutdownAction.Hibernate;
        await Make(o).CheckAsync(default);
        Assert.Equal(ShutdownAction.Hibernate, _exec.Calls.Single().Action);
        Assert.StartsWith("Hibernating", _sink.Events.Single().Message);
    }

    [Fact]
    public async Task Final_delay_is_waited_before_shutting_down()
    {
        _store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        var o = Opts();
        o.FinalDelaySeconds = 0.5;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Make(o).CheckAsync(default);
        Assert.True(sw.ElapsedMilliseconds >= 450);
        Assert.Single(_exec.Calls);
    }
}

/// <summary>Monitor + coordinator against the simulator: the whole chain, in test mode.</summary>
public sealed class EndToEndShutdownTests : IAsyncLifetime
{
    private SimulatorServer _server = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _running = [];
    private readonly UpsStateStore _store = new();
    private readonly RecordingSink _sink = new();
    private readonly RecordingExecutor _exec = new();

    public ValueTask InitializeAsync()
    {
        _server = new SimulatorServer([new SimulatedUps("ups", UpsProfile.Load("back-ups")) { Jitter = false }],
            new SimulatorOptions { Port = 0 });
        _server.StartListening();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_running);
        await _server.DisposeAsync();
    }

    private void Start(ShutdownOptions shutdown)
    {
        var endpoint = new EndpointOptions
        {
            Host = "127.0.0.1", Port = _server.Port, Ups = "ups", PollIntervalSeconds = 0.1, TimeoutSeconds = 2,
            Role = UpsRole.PowersThisMachine, Username = "monuser", Password = "secret",
        };
        var monitor = new UpsMonitor("nas", endpoint, new ReconnectOptions { InitialDelaySeconds = 0.1, MaxDelaySeconds = 0.3 },
            _store, [_sink], NullLogger.Instance);
        var coordinator = new ShutdownCoordinator(
            TestOptions.Fixed(new WallNutOptions { Endpoints = { ["nas"] = endpoint }, Shutdown = shutdown }),
            _store, [_sink], _exec, NullLogger<ShutdownCoordinator>.Instance, TimeProvider.System)
        { CheckInterval = TimeSpan.FromMilliseconds(50) };

        _running.Add(Task.Run(() => monitor.RunAsync(_stop.Token)));
        _running.Add(coordinator.StartAsync(_stop.Token));
    }

    [Fact]
    public async Task Outage_until_low_battery_triggers_shutdown()
    {
        Start(new ShutdownOptions { DryRun = true, FinalDelaySeconds = 0 });
        await _sink.WaitFor(UpsEventKind.Connected);

        _server["ups"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery);
        await Task.Delay(300);
        Assert.Empty(_exec.Calls); // on battery but not low yet

        _server["ups"].SetCharge(8); // below the UPS's 10% low-battery point
        await _sink.WaitFor(UpsEventKind.LowBattery);
        var e = await _sink.WaitFor(UpsEventKind.ShutdownInitiated);
        Assert.Contains("low battery", e.Message);
        Assert.Single(_exec.Calls);
    }

    [Fact]
    public async Task Losing_contact_mid_outage_triggers_the_fail_safe()
    {
        Start(new ShutdownOptions { DryRun = true, FinalDelaySeconds = 0, LostWhileOnBatterySeconds = 1 });
        await _sink.WaitFor(UpsEventKind.Connected);

        _server["ups"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery);

        // The network to the NAS dies while it's on battery.
        _server.StopListening();
        _server.DropAllClients();
        await _sink.WaitFor(UpsEventKind.CommunicationLost);
        var e = await _sink.WaitFor(UpsEventKind.ShutdownInitiated, timeoutMs: 5000);
        Assert.Contains("last seen on battery", e.Message);
    }

    [Fact]
    public async Task Nas_forced_shutdown_is_obeyed()
    {
        Start(new ShutdownOptions { DryRun = true, FinalDelaySeconds = 0 });
        await _sink.WaitFor(UpsEventKind.Connected);
        _server["ups"].SetForcedShutdown();
        await _sink.WaitFor(UpsEventKind.ForcedShutdown);
        var e = await _sink.WaitFor(UpsEventKind.ShutdownInitiated);
        Assert.Contains("FSD", e.Message);
    }

    [Fact]
    public async Task Power_returning_before_the_limit_means_no_shutdown()
    {
        Start(new ShutdownOptions { DryRun = true, FinalDelaySeconds = 0, OnBatteryForSeconds = 2 });
        await _sink.WaitFor(UpsEventKind.Connected);
        _server["ups"].PowerFail();
        await _sink.WaitFor(UpsEventKind.OnBattery);
        await Task.Delay(800);
        _server["ups"].PowerRestore();
        await _sink.WaitFor(UpsEventKind.OnLine);
        await Task.Delay(2000);
        Assert.Empty(_exec.Calls);
    }
}
