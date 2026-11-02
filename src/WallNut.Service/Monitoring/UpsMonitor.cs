using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WallNut.Nut;
using WallNut.Service.Configuration;

namespace WallNut.Service.Monitoring;

/// <summary>
/// Watches one UPS on one NUT server over its own connection: connects, optionally logs in like
/// upsmon, polls every few seconds, turns status changes into events, and reconnects forever
/// with backoff when anything goes wrong. The host can be an IP address or a DNS name; names are
/// looked up again on every connection attempt, so a server whose address changes is followed.
/// </summary>
public sealed class UpsMonitor
{
    private readonly string _id;
    private readonly EndpointOptions _endpoint;
    private readonly ReconnectOptions _reconnect;
    private readonly UpsStateStore _store;
    private readonly IReadOnlyList<IUpsEventSink> _sinks;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public string DisplayName { get; }

    public UpsMonitor(string id, EndpointOptions endpoint, ReconnectOptions reconnect, UpsStateStore store,
        IEnumerable<IUpsEventSink> sinks, ILogger log, TimeProvider? time = null)
    {
        _id = id;
        _endpoint = endpoint;
        _reconnect = reconnect;
        _store = store;
        _sinks = sinks.ToList();
        _log = log;
        _time = time ?? TimeProvider.System;
        DisplayName = string.IsNullOrWhiteSpace(endpoint.DisplayName) ? id : endpoint.DisplayName;

        _store.Set(new UpsState
        {
            EndpointId = id, DisplayName = DisplayName, Host = endpoint.Host, Port = endpoint.Port,
            Ups = endpoint.Ups, Role = endpoint.Role,
        });
    }

    private UpsState State => _store.Get(_id)!;
    private TimeSpan Timeout => TimeSpan.FromSeconds(_endpoint.TimeoutSeconds);
    private TimeSpan PollInterval => TimeSpan.FromSeconds(_endpoint.PollIntervalSeconds);
    private string Where => $"{_endpoint.Ups}@{_endpoint.Host}:{_endpoint.Port}";

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(
            TimeSpan.FromSeconds(_reconnect.InitialDelaySeconds), TimeSpan.FromSeconds(_reconnect.MaxDelaySeconds));
        bool everConnected = false, lostReported = false;

        while (!ct.IsCancellationRequested)
        {
            NutClient? client = null;
            try
            {
                client = await NutClient.ConnectAsync(_endpoint.Host, _endpoint.Port, Timeout, ct);
                if (_endpoint.Username is { } user) await client.AuthenticateAsync(user, _endpoint.Password!, ct);
                if (_endpoint.Role == UpsRole.PowersThisMachine) await client.LoginAsync(_endpoint.Ups, ct);

                // The first read proves the UPS name is right before we call it connected.
                var vars = await ReadAsync(client, ct);
                backoff.Reset();
                var now = _time.GetUtcNow();
                _store.Set(State with { Connected = true, ConnectedSince = now, LastError = null });
                await PublishAsync(everConnected ? UpsEventKind.CommunicationRestored : UpsEventKind.Connected,
                    everConnected ? $"Contact with {Where} is back." : $"Connected to {Where}.", State.Status, ct);
                everConnected = true;
                lostReported = false;

                await ApplyAsync(vars, ct);
                await PollAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                var why = Explain(e);
                _store.Set(State with { Connected = false, ConnectedSince = null, LastError = why });
                if (!lostReported)
                {
                    var wasOnBattery = State.Status?.IsOnBattery == true ? " The UPS was last seen ON BATTERY." : "";
                    await PublishAsync(UpsEventKind.CommunicationLost,
                        (everConnected ? $"Lost contact with {Where}: " : $"Can't reach {Where}: ") + why + wasOnBattery,
                        State.Status, ct);
                    lostReported = true;
                }

                var delay = backoff.Next();
                _log.LogDebug("[{Endpoint}] Retry {Attempt} in {Delay:0.0}s ({Why})", DisplayName, backoff.Failures, delay.TotalSeconds, why);
                try { await Task.Delay(delay, _time, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (client is not null)
                {
                    if (ct.IsCancellationRequested && client.IsConnected)
                    {
                        using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await client.LogoutAsync(bye.Token);
                    }
                    await client.DisposeAsync();
                }
            }
        }
    }

    /// <summary>Polls until the connection fails (throws) or the service stops.</summary>
    private async Task PollAsync(NutClient client, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(PollInterval, _time, ct);
            try
            {
                var vars = await ReadAsync(client, ct);
                if (State.Stale)
                {
                    _store.Set(State with { Stale = false });
                    await PublishAsync(UpsEventKind.DataRestored, $"{Where} is reporting data again.", State.Status, ct);
                }
                await ApplyAsync(vars, ct);
            }
            catch (NutException e) when (e.Code is NutErrorCode.DataStale or NutErrorCode.DriverNotConnected)
            {
                // The server is fine but has lost the UPS itself. Keep the connection and keep asking.
                if (!State.Stale)
                {
                    _store.Set(State with { Stale = true, LastError = Explain(e) });
                    await PublishAsync(UpsEventKind.DataStale, $"{Where}: {Explain(e)}", State.Status, ct);
                }
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadAsync(NutClient client, CancellationToken ct) =>
        await client.ListVarsAsync(_endpoint.Ups, ct);

    private async Task ApplyAsync(IReadOnlyDictionary<string, string> vars, CancellationToken ct)
    {
        var previous = State.Status;
        var status = UpsStatus.Parse(vars.GetValueOrDefault("ups.status"));
        _store.Set(State with { Vars = vars, Status = status, LastUpdate = _time.GetUtcNow() });

        foreach (var kind in EventDetector.Detect(previous, status))
            await PublishAsync(kind, Describe(kind, vars), status, ct);
    }

    private async Task PublishAsync(UpsEventKind kind, string message, UpsStatus? status, CancellationToken ct)
    {
        var e = new UpsEvent(_id, DisplayName, kind, _time.GetUtcNow(), status, message);
        foreach (var sink in _sinks)
        {
            try { await sink.PublishAsync(e, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken sink (say, a webhook) must never stop monitoring.
                _log.LogError(ex, "[{Endpoint}] Event sink {Sink} failed for {Kind}", DisplayName, sink.GetType().Name, kind);
            }
        }
    }

    private static string Describe(UpsEventKind kind, IReadOnlyDictionary<string, string> v)
    {
        string charge = v.TryGetValue("battery.charge", out var c) ? $"battery {c}%" : "battery level unknown";
        string runtime = v.TryGetValue("battery.runtime", out var r) && double.TryParse(r, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            ? $", about {TimeSpan.FromSeconds(secs):h\\:mm\\:ss} left" : "";
        return kind switch
        {
            UpsEventKind.OnBattery => $"Power failure: running on battery ({charge}{runtime}).",
            UpsEventKind.OnLine => $"Power is back ({charge}).",
            UpsEventKind.LowBattery => $"Battery is low ({charge}{runtime}).",
            UpsEventKind.ForcedShutdown => "The NUT server is forcing a shutdown.",
            UpsEventKind.ReplaceBattery => "The UPS says its battery needs replacing.",
            UpsEventKind.ReplaceBatteryCleared => "The replace-battery warning has cleared.",
            UpsEventKind.Overload => $"The UPS is overloaded (load {v.GetValueOrDefault("ups.load", "?")}%).",
            UpsEventKind.OverloadCleared => "The overload has cleared.",
            UpsEventKind.Off => "The UPS has turned its outlets off.",
            UpsEventKind.NotOff => "The UPS outlets are on again.",
            UpsEventKind.Bypass => "The UPS is on bypass: the load is NOT protected.",
            UpsEventKind.NotBypass => "The UPS is off bypass.",
            UpsEventKind.Calibrating => "The UPS is calibrating its battery.",
            UpsEventKind.NotCalibrating => "Battery calibration finished.",
            UpsEventKind.Alarm => $"UPS alarm: {v.GetValueOrDefault("ups.alarm", "no details")}",
            UpsEventKind.AlarmCleared => "The UPS alarm has cleared.",
            _ => kind.ToString(),
        };
    }

    /// <summary>A plain-words reason for a failure, with a hint where we know the usual fix.</summary>
    public static string Explain(Exception e)
    {
        var root = e;
        while (root.InnerException is not null && root is not SocketException) root = root.InnerException;
        return root switch
        {
            NutException { Code: NutErrorCode.AccessDenied } =>
                "the server refused access. On a Synology NAS, add this computer's IP address under " +
                "Control Panel > Hardware & Power > UPS > Permitted DiskStation Devices.",
            NutException { Code: NutErrorCode.UnknownUps } => "the server has no UPS by that name. Check the Ups setting (Synology uses \"ups\").",
            NutException { Code: NutErrorCode.InvalidPassword or NutErrorCode.InvalidUsername } => "the username or password was rejected.",
            NutException { Code: NutErrorCode.DataStale or NutErrorCode.DriverNotConnected } =>
                "the server has lost contact with the UPS itself (is its USB cable connected?).",
            NutException n => $"the server returned an error ({n.RawCode}).",
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
                "couldn't find that host name. Check the spelling, or use the server's IP address.",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                "the connection was refused. Is the NUT server enabled (Synology: Enable network UPS server)?",
            SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
                "the network can't reach that address.",
            SocketException { SocketErrorCode: SocketError.ConnectionReset } => "the server reset the connection.",
            SocketException s => $"network error ({s.SocketErrorCode}).",
            TimeoutException => "no response in time.",
            IOException => "the server closed the connection.",
            _ => root.Message,
        };
    }
}
