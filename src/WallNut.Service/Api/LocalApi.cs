using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;
using WallNut.Shared;

namespace WallNut.Service.Api;

/// <summary>
/// The service's local-only API for the tray app and desktop app. It listens on a named pipe
/// (Windows) or a Unix socket (macOS/Linux), never on a network port.
/// </summary>
public static class LocalApi
{
    /// <summary>Listens on the pipe/socket only.</summary>
    public static void UseLocalEndpoint(this WebApplicationBuilder builder, string endpoint)
    {
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (ServiceEndpoint.IsSocketPath(endpoint))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
                if (File.Exists(endpoint)) File.Delete(endpoint); // left over from a crash
                k.ListenUnixSocket(endpoint);
            }
            else
            {
                k.ListenNamedPipe(endpoint);
            }
        });

        if (OperatingSystem.IsWindows())
        {
            var security = PipeSecurityForUsers();
            builder.WebHost.UseNamedPipes(o =>
            {
                // When running as SYSTEM, logged-in users still need to read status and events.
                o.CurrentUserOnly = false;
                o.PipeSecurity = security;
            });
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static PipeSecurity PipeSecurityForUsers()
    {
        var security = new PipeSecurity();
        void Allow(SecurityIdentifier who, PipeAccessRights rights) =>
            security.AddAccessRule(new PipeAccessRule(who, rights, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);
        Allow(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite);
        return security;
    }

    /// <summary>On Unix, let logged-in users connect to the socket once it exists.</summary>
    public static void OpenSocketToUsers(this WebApplication app, string endpoint)
    {
        if (!ServiceEndpoint.IsSocketPath(endpoint) || OperatingSystem.IsWindows()) return;
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!OperatingSystem.IsWindows()) OpenToEveryone(app, endpoint);
        });
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void OpenToEveryone(WebApplication app, string socket)
    {
        try
        {
            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch (Exception e) { app.Logger.LogWarning(e, "Could not open {Socket} to other users", socket); }
    }

    public static void MapLocalApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapHistoryApi();
        api.MapSettingsApi();
        api.MapActionsApi();

        api.MapGet("/info", (UpsStateStore store, IOptionsMonitor<WallNutOptions> options, ShutdownCoordinator coordinator) =>
        {
            var o = options.CurrentValue.Shutdown;
            return new ServiceInfo(
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                Environment.MachineName,
                o.Enabled, o.DryRun, o.Action.ToString(), coordinator.Initiated,
                store.All.Select(ToSummary).ToList());
        });

        api.MapGet("/events/recent", (EventHub hub) => hub.Recent());

        api.MapGet("/events/stream", async (HttpContext http, EventHub hub, IHostApplicationLifetime life) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            using var subscription = hub.Subscribe();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, life.ApplicationStopping);
            var ct = cts.Token;

            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Wake at least every 15 s to send a keep-alive, so dead connections are noticed.
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    bool hasEvent;
                    try { hasEvent = await subscription.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { hasEvent = false; }

                    if (!hasEvent)
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct);
                    }
                    while (subscription.Reader.TryRead(out var e))
                    {
                        var json = JsonSerializer.Serialize(e, WallNutServiceClient.Json);
                        await http.Response.WriteAsync($"id: {e.Sequence.ToString(CultureInfo.InvariantCulture)}\ndata: {json}\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* app disconnected or service stopping */ }
            catch (IOException) { /* app went away */ }
        });

        var lastTest = DateTimeOffset.MinValue;
        api.MapPost("/notifications/test", async (IEnumerable<IUpsEventSink> sinks, TimeProvider time, CancellationToken ct) =>
        {
            // Harmless, but don't let anything spam it.
            var now = time.GetUtcNow();
            if (now - lastTest < TimeSpan.FromSeconds(2)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            lastTest = now;

            var e = new UpsEvent(UpsEvent.ThisMachine, Environment.MachineName, UpsEventKind.Test, now, null,
                "This is a test notification from WALLNut. If you can see it, notifications are working.");
            foreach (var sink in sinks) await sink.PublishAsync(e, ct);
            return Results.Accepted();
        });
    }

    public static UpsSummary ToSummary(UpsState s)
    {
        static double? N(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return new UpsSummary(
            s.EndpointId, s.DisplayName, s.Host, s.Port, s.Ups, s.Role.ToString(), s.Connected, s.Stale,
            s.Status?.Raw, s.Status?.IsOnBattery == true,
            N(s.Var("battery.charge")), N(s.Var("battery.runtime")), N(s.Var("ups.load")),
            s.Var("device.mfr") ?? s.Var("ups.mfr"), s.Var("device.model") ?? s.Var("ups.model"),
            s.Var("device.serial") ?? s.Var("ups.serial"),
            s.LastUpdate, s.OnBatterySince, s.LastError,
            N(s.Var("input.voltage")) ?? N(s.Var("input.L1-N.voltage")));
    }
}
