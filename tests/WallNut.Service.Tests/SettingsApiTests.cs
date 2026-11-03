using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WallNut.Service.Api;
using WallNut.Shared;
using WallNut.Shared.Settings;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

internal sealed class FakeCaller(bool admin) : ICallerIdentity
{
    public (string? User, bool IsAdmin) Get(HttpContext http) => (admin ? "TEST\\admin" : "TEST\\user", admin);
}

/// <summary>Settings changes through the API, applied by the running service without a restart.</summary>
public sealed class SettingsApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wallnut-settings-{Guid.NewGuid():N}");
    private readonly List<WebApplication> _apps = [];
    private SimulatorServer _sim = null!;
    private string _config = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _sim = new SimulatorServer(
        [
            new SimulatedUps("ups", UpsProfile.Load("back-ups")) { Jitter = false },
            new SimulatedUps("rack", UpsProfile.Load("full-featured")) { Jitter = false },
        ], new SimulatorOptions { Port = 0 });
        _sim.StartListening();
        _config = Path.Combine(_dir, "config.json");
        File.WriteAllText(_config, $$"""
            {
              // A hand-written comment; the file is rewritten without comments.
              "WallNut": {
                "Endpoints": {
                  "office": { "DisplayName": "Office", "Host": "127.0.0.1", "Port": {{_sim.Port}}, "Ups": "ups",
                              "Username": "monuser", "Password": "secret", "Role": "PowersThisMachine", "PollIntervalSeconds": 0.2 }
                },
                "Shutdown": { "DryRun": true },
                "SomethingElse": { "Kept": true }
              }
            }
            """);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _apps) { await a.StopAsync(); await a.DisposeAsync(); }
        await _sim.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    private async Task<(WallNutServiceClient Client, WebApplication App)> Start(bool? admin)
    {
        var endpoint = TestEndpoint.New();
        var app = ServiceHost.Build(
            ["--environment", "Testing", $"--WallNut:Api:Endpoint={endpoint}", "--WallNut:History:Enabled=false",
             "--Logging:LogLevel:Default=Warning"],
            _config,
            services => { if (admin is { } a) services.AddSingleton<ICallerIdentity>(new FakeCaller(a)); });
        _apps.Add(app);
        await app.StartAsync();
        return (new WallNutServiceClient(endpoint), app);
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
    public async Task Real_identity_check_sees_who_is_calling()
    {
        var (c, _) = await Start(admin: null); // the real pipe/socket check
        var me = await c.GetCallerAsync();
        Assert.Contains(Environment.UserName, me.User ?? "", StringComparison.OrdinalIgnoreCase);
        // Here the service runs as the same account as the caller, which may change settings
        // (it could edit the file anyway). An installed service runs as SYSTEM, so ordinary users can't.
        Assert.True(me.IsAdmin);
    }

    [Fact]
    public async Task Ordinary_users_can_read_but_not_change()
    {
        var (c, _) = await Start(admin: false);
        var view = await c.GetSettingsAsync();
        Assert.False(view.CanEdit);
        Assert.Contains("administrator", view.ReadOnlyReason);
        var office = Assert.Single(view.Endpoints);
        Assert.Equal("office", office.Id);
        Assert.True(office.PasswordSet);
        Assert.Null(office.Password); // never sent to apps

        var ex = await Assert.ThrowsAsync<SettingsException>(() => c.SaveEndpointAsync(office with { DisplayName = "x" }));
        Assert.Contains("administrator", ex.Message);
        await Assert.ThrowsAsync<SettingsException>(() => c.DeleteEndpointAsync("office"));
        await Assert.ThrowsAsync<SettingsException>(() => c.SaveShutdownAsync(view.Shutdown with { DryRun = false }));
    }

    [Fact]
    public async Task Adding_a_ups_starts_monitoring_it_without_a_restart()
    {
        var (c, _) = await Start(admin: true);
        await Eventually(() => c.GetInfoAsync(), i => i.Ups.Any(u => u.Id == "office" && u.Connected));

        await c.SaveEndpointAsync(new EndpointConfig
        {
            Id = "rack", DisplayName = "Rack UPS", Host = "localhost", Port = _sim.Port, Ups = "rack", PollIntervalSeconds = 0.2,
        });

        var info = await Eventually(() => c.GetInfoAsync(), i => i.Ups.Any(u => u.Id == "rack" && u.Connected));
        Assert.Equal("FX-3000RT", info.Ups.Single(u => u.Id == "rack").Model);

        var detail = await c.GetUpsDetailAsync("rack");
        Assert.True(detail.Variables.Count > 300);
        Assert.Equal("WNL3K25031400042", detail.Variables["ups.serial"]);

        // The file keeps what the app didn't touch.
        var text = File.ReadAllText(_config);
        Assert.Contains("SomethingElse", text);
        Assert.Contains("\"rack\"", text);
        Assert.True(File.Exists(_config + ".bak"));
    }

    [Fact]
    public async Task Editing_keeps_the_stored_password_unless_a_new_one_is_sent()
    {
        var (c, _) = await Start(admin: true);
        var office = (await c.GetSettingsAsync()).Endpoints.Single();
        await c.SaveEndpointAsync(office with { DisplayName = "Front office" }); // Password = null: keep it

        var text = File.ReadAllText(_config);
        Assert.Contains("Front office", text);
        Assert.Contains("\"secret\"", text);
        await Eventually(() => c.GetInfoAsync(), i => i.Ups.Any(u => u.DisplayName == "Front office" && u.Connected));
    }

    [Fact]
    public async Task Removing_a_ups_stops_monitoring_it()
    {
        var (c, _) = await Start(admin: true);
        await Eventually(() => c.GetInfoAsync(), i => i.Ups.Any(u => u.Id == "office" && u.Connected));
        await c.DeleteEndpointAsync("office");
        await Eventually(() => c.GetInfoAsync(), i => i.Ups.Count == 0);
        await Eventually(() => Task.FromResult(_sim.ClientCount), n => n == 0);
    }

    [Fact]
    public async Task Invalid_changes_are_refused_with_reasons()
    {
        var (c, _) = await Start(admin: true);

        var ex = await Assert.ThrowsAsync<SettingsException>(() => c.SaveEndpointAsync(new EndpointConfig
        {
            Id = "nas2", Host = "10.0.0.5", Ups = "ups", Role = "PowersThisMachine", // no username
        }));
        Assert.Contains(ex.Problems, p => p.Contains("monuser"));

        ex = await Assert.ThrowsAsync<SettingsException>(() => c.SaveEndpointAsync(new EndpointConfig { Id = "bad id!", Host = "x" }));
        Assert.Contains(ex.Problems, p => p.Contains("letters, digits"));

        ex = await Assert.ThrowsAsync<SettingsException>(() => c.SaveShutdownAsync(new ShutdownSettings { MinimumSupplies = 5 }));
        Assert.Contains(ex.Problems, p => p.Contains("MinimumSupplies"));

        Assert.DoesNotContain("nas2", File.ReadAllText(_config));
    }

    [Fact]
    public async Task Turning_test_mode_off_applies_immediately()
    {
        var (c, _) = await Start(admin: true);
        Assert.True((await c.GetInfoAsync()).ShutdownTestMode);

        var shutdown = (await c.GetSettingsAsync()).Shutdown;
        await c.SaveShutdownAsync(shutdown with { DryRun = false, BatteryChargeBelowPercent = 30 });

        await Eventually(() => c.GetInfoAsync(), i => !i.ShutdownTestMode);
        var saved = (await c.GetSettingsAsync()).Shutdown;
        Assert.Equal(30, saved.BatteryChargeBelowPercent);
    }

    [Fact]
    public async Task Connection_test_reports_what_it_finds()
    {
        var (c, _) = await Start(admin: false); // testing is allowed for everyone

        var ok = await c.TestEndpointAsync(new EndpointConfig
        {
            Host = "localhost", Port = _sim.Port, Ups = "ups", Username = "monuser", Password = "secret", Role = "PowersThisMachine",
        });
        Assert.True(ok.Ok, ok.Message);
        Assert.True(ok.LoginWorks);
        Assert.Equal("Back-UPS ES 600M1", ok.Model);
        Assert.Equal(["rack", "ups"], ok.AvailableUps.Order());

        var wrongName = await c.TestEndpointAsync(new EndpointConfig { Host = "127.0.0.1", Port = _sim.Port, Ups = "UPS" });
        Assert.False(wrongName.Ok);
        Assert.Contains("This server has: ups, rack", wrongName.Message.Replace("rack, ups", "ups, rack"));

        var badLogin = await c.TestEndpointAsync(new EndpointConfig
        {
            Host = "127.0.0.1", Port = _sim.Port, Ups = "ups", Username = "monuser", Password = "nope", Role = "PowersThisMachine",
        });
        Assert.False(badLogin.Ok);

        var stored = await c.TestEndpointAsync(new EndpointConfig
        {
            Id = "office", Host = "127.0.0.1", Port = _sim.Port, Ups = "ups", Username = "monuser", Role = "PowersThisMachine",
        });
        Assert.True(stored.Ok, "should use the stored password: " + stored.Message);

        var unknownHost = await c.TestEndpointAsync(new EndpointConfig { Host = "no-such-nas.invalid", Ups = "ups" });
        Assert.Contains("couldn't find that host name", unknownHost.Message);
    }
}
