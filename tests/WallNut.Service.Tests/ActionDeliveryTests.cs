using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WallNut.Service.Actions;
using WallNut.Service.Api;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Shared;
using WallNut.Shared.Actions;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

/// <summary>A local HTTP server standing in for ntfy and webhook receivers.</summary>
internal sealed class FakeReceiver : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    private readonly WebApplication _app;
    public ConcurrentQueue<Request> Requests { get; } = new();
    /// <summary>Status codes to answer with, in order; once used up, 200.</summary>
    public ConcurrentQueue<int> Responses { get; } = new();
    public string Url { get; }

    private FakeReceiver(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<FakeReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeReceiver? self = null;
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Requests.Enqueue(new Request(ctx.Request.Method, ctx.Request.Path,
                ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            ctx.Response.StatusCode = self.Responses.TryDequeue(out var code) ? code : 200;
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeReceiver(app, url);
        return self;
    }

    public async Task<List<Request>> WaitFor(int count, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (Requests.Count < count && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        return Requests.ToList();
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}

/// <summary>Actions run by the real service, delivering to a local receiver.</summary>
public sealed class ActionDeliveryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wallnut-actions-{Guid.NewGuid():N}");
    private readonly List<WebApplication> _apps = [];
    private SimulatorServer _sim = null!;
    private FakeReceiver _rx = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _sim = new SimulatorServer(
        [
            new SimulatedUps("ups", UpsProfile.Load("back-ups")) { Jitter = false },
            new SimulatedUps("rack", UpsProfile.Load("full-featured")) { Jitter = false },
        ], new SimulatorOptions { Port = 0 });
        _sim.StartListening();
        _rx = await FakeReceiver.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _apps) { await a.StopAsync(); await a.DisposeAsync(); }
        await _rx.DisposeAsync();
        await _sim.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    private async Task<(WallNutServiceClient Client, WebApplication App)> Start(string actionsJson, bool admin = true)
    {
        var config = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, $$"""
            {
              "WallNut": {
                "Endpoints": {
                  "office": { "DisplayName": "Office", "Host": "127.0.0.1", "Port": {{_sim.Port}}, "Ups": "ups", "PollIntervalSeconds": 0.1 },
                  "rack": { "DisplayName": "Rack", "Host": "127.0.0.1", "Port": {{_sim.Port}}, "Ups": "rack", "PollIntervalSeconds": 0.1 }
                },
                "Actions": {{actionsJson}}
              }
            }
            """);
        var endpoint = OperatingSystem.IsWindows() ? $"WallNut.Test.{Guid.NewGuid():N}" : Path.Combine(_dir, $"{Guid.NewGuid():N}.sock");
        var app = ServiceHost.Build(
            ["--environment", "Testing", $"--WallNut:Api:Endpoint={endpoint}", "--WallNut:History:Enabled=false", "--Logging:LogLevel:Default=Warning"],
            config,
            services =>
            {
                services.AddSingleton<ICallerIdentity>(new FakeCaller(admin));
                // Short retry waits for tests.
                services.AddSingleton(sp => new ActionDispatcher(
                    sp.GetRequiredService<IOptionsMonitor<WallNutOptions>>(), sp.GetRequiredService<UpsStateStore>(),
                    sp.GetServices<IActionSender>(), sp, sp.GetRequiredService<ILogger<ActionDispatcher>>(), TimeProvider.System)
                { Retries = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)] });
            });
        _apps.Add(app);
        await app.StartAsync();
        var client = new WallNutServiceClient(endpoint);
        await Eventually(() => client.GetInfoAsync(), i => i.Ups.Count == 2 && i.Ups.All(u => u.Connected));
        return (client, app);
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = await get();
            if (ok(v)) return v;
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Outage_reaches_ntfy_and_a_webhook()
    {
        await Start($$"""
            {
              "phone": { "Type": "Ntfy", "Name": "My phone", "Ntfy": { "Server": "{{_rx.Url}}", "Topic": "wallnut-test", "Token": "tk_123" } },
              "hook": { "Type": "Webhook", "Webhook": { "Url": "{{_rx.Url}}/hook", "Secret": "s3cret", "Headers": { "X-Api-Key": "abc" } } }
            }
            """);
        _sim["ups"].PowerFail();
        var requests = await _rx.WaitFor(2);

        var ntfy = Assert.Single(requests, r => r.Path == "/");
        Assert.Equal("Bearer tk_123", ntfy.Headers["Authorization"]);
        using (var n = JsonDocument.Parse(ntfy.Body))
        {
            Assert.Equal("wallnut-test", n.RootElement.GetProperty("topic").GetString());
            Assert.Equal("Power failure: Office", n.RootElement.GetProperty("title").GetString());
            Assert.Equal(4, n.RootElement.GetProperty("priority").GetInt32());
            Assert.Equal("zap", n.RootElement.GetProperty("tags")[0].GetString());
        }

        var hook = Assert.Single(requests, r => r.Path == "/hook");
        Assert.Equal("abc", hook.Headers["X-Api-Key"]);
        var expected = "sha256=" + WebhookSender.Sign("s3cret", hook.Headers["X-WallNut-Timestamp"], hook.Body);
        Assert.Equal(expected, hook.Headers["X-WallNut-Signature"]);
        using var w = JsonDocument.Parse(hook.Body);
        Assert.Equal("OnBattery", w.RootElement.GetProperty("event").GetString());
        Assert.Equal("office", w.RootElement.GetProperty("ups").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Short_power_blip_sends_nothing_when_delayed()
    {
        await Start($$"""{ "phone": { "Type": "Ntfy", "DelaySeconds": 2, "Ntfy": { "Server": "{{_rx.Url}}", "Topic": "t" } } }""");
        _sim["ups"].PowerFail();
        await Task.Delay(700);
        _sim["ups"].PowerRestore();
        await Task.Delay(3000);
        Assert.Empty(_rx.Requests);

        _sim["ups"].PowerFail(); // this time it lasts
        var r = await _rx.WaitFor(1, 6000);
        Assert.Contains("Power failure", Assert.Single(r).Body);
    }

    [Fact]
    public async Task Action_for_one_ups_ignores_the_others()
    {
        await Start($$"""{ "rackonly": { "Type": "Webhook", "Ups": ["rack"], "Webhook": { "Url": "{{_rx.Url}}/hook" } } }""");
        _sim["ups"].PowerFail();
        await Task.Delay(1500);
        Assert.Empty(_rx.Requests);
        _sim["rack"].PowerFail();
        Assert.Single(await _rx.WaitFor(1));
    }

    [Fact]
    public async Task Failures_are_retried_then_reported()
    {
        var (client, _) = await Start($$"""{ "hook": { "Type": "Webhook", "Name": "Flaky", "Webhook": { "Url": "{{_rx.Url}}/hook" } } }""");
        _rx.Responses.Enqueue(500);
        _rx.Responses.Enqueue(503);
        _sim["ups"].PowerFail();
        Assert.Equal(3, (await _rx.WaitFor(3)).Count); // two failures, then success
        Assert.DoesNotContain(await client.GetRecentEventsAsync(), e => e.Kind == "ActionFailed");

        // Now it never works: 3 tries, then an "ActionFailed" event.
        for (var i = 0; i < 3; i++) _rx.Responses.Enqueue(500);
        _sim["ups"].PowerRestore();
        var failed = await Eventually(() => client.GetRecentEventsAsync(), r => r.Any(e => e.Kind == "ActionFailed"));
        Assert.Contains("Flaky", failed.First(e => e.Kind == "ActionFailed").Message);
    }

    [Fact]
    public async Task Settings_keep_secrets_hidden_and_test_button_works()
    {
        var (client, _) = await Start("{}");
        await client.SaveActionAsync(new ActionConfig
        {
            Id = "phone", Name = "My phone", Type = ActionTypes.Ntfy,
            Ntfy = new NtfyConfig { Server = _rx.Url, Topic = "wallnut-abc", Token = "tk_secret" },
        });

        var view = (await Eventually(() => client.GetSettingsAsync(), s => s.Actions?.Count == 1)).Actions!.Single();
        Assert.Null(view.Ntfy!.Token);
        Assert.True(view.Ntfy.TokenSet);
        Assert.Equal(ActionDefaults.Events, view.Events);

        // Re-saving without the token keeps it.
        await client.SaveActionAsync(view with { Name = "Phone" });
        Assert.Contains("tk_secret", File.ReadAllText(Directory.GetFiles(_dir, "config-*.json").Single()));

        var test = await client.TestActionAsync(view);
        Assert.True(test.Ok, test.Message);
        var sent = Assert.Single(await _rx.WaitFor(1));
        Assert.Equal("Bearer tk_secret", sent.Headers["Authorization"]);
        Assert.Contains("WALLNut test", sent.Body);
    }

    [Fact]
    public async Task Only_administrators_change_or_test_actions()
    {
        var (client, _) = await Start("{}", admin: false);
        var action = new ActionConfig { Id = "phone", Ntfy = new NtfyConfig { Server = _rx.Url, Topic = "t" } };
        await Assert.ThrowsAsync<SettingsException>(() => client.SaveActionAsync(action));
        Assert.False((await client.TestActionAsync(action)).Ok);
        Assert.Empty(_rx.Requests);
    }

    [Fact]
    public async Task Test_notification_button_does_not_fire_actions()
    {
        var (client, _) = await Start($$"""{ "hook": { "Type": "Webhook", "Events": ["Test", "OnBattery"], "Webhook": { "Url": "{{_rx.Url}}/hook" } } }""");
        await client.SendTestAsync();
        await Task.Delay(1000);
        Assert.Empty(_rx.Requests);
    }

    [Fact]
    public async Task Broken_action_never_stops_monitoring()
    {
        var (client, _) = await Start("""{ "bad": { "Type": "Script", "Script": { "Path": "C:\\nope\\missing.ps1" } } }""");
        Assert.All((await client.GetInfoAsync()).Ups, u => Assert.True(u.Connected));
    }
}
