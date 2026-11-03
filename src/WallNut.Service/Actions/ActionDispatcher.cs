using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Shared;
using WallNut.Shared.Actions;

namespace WallNut.Service.Actions;

/// <summary>
/// Runs alerts and actions. Each configured action gets its own event filter, "only if it lasts"
/// hold, flood limit and background queue with retries, so a slow or failing action never holds up
/// monitoring, shutdown, or the other actions. Settings changes apply immediately.
/// </summary>
public sealed class ActionDispatcher(
    IOptionsMonitor<WallNutOptions> options,
    UpsStateStore states,
    IEnumerable<IActionSender> senders,
    IServiceProvider services,
    ILogger<ActionDispatcher> log,
    TimeProvider time) : BackgroundService, IUpsEventSink
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private readonly Dictionary<string, IActionSender> _senders = senders.ToDictionary(s => s.Type, StringComparer.Ordinal);
    private readonly ILogger _log = log;
    private readonly TimeProvider _time = time;
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationToken _stopping;

    /// <summary>Retry waits (tests shorten them).</summary>
    public TimeSpan[] Retries { get; init; } = RetryDelays;

    /// <summary>The sender for an action type (used by the Test button).</summary>
    public IActionSender? SenderFor(string type) => _senders.GetValueOrDefault(type);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        using var subscription = options.OnChange(changed => Rebuild());
        Rebuild();
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var w in _workers.Values) w.Dispose();
            _workers.Clear();
        }
    }

    /// <summary>Starts, restarts or stops workers to match the current settings.</summary>
    private void Rebuild()
    {
        var config = options.CurrentValue;
        lock (_gate)
        {
            var wanted = new Dictionary<string, (ActionOptions Options, string Fingerprint)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, a) in config.Actions.Where(a => a.Value.Enabled))
            {
                var problems = ActionValidation.Validate(id, a);
                if (problems.Count > 0) { foreach (var p in problems) _log.LogWarning("Not running: {Problem}", p); continue; }
                if (!_senders.ContainsKey(a.Type)) { _log.LogWarning("Action '{Id}': {Type} isn't available in this version.", id, a.Type); continue; }
                wanted[id] = (a, JsonSerializer.Serialize(a));
            }

            foreach (var id in _workers.Keys.Where(id => !wanted.TryGetValue(id, out var w) || w.Fingerprint != _workers[id].Fingerprint).ToList())
            {
                _workers[id].Dispose();
                _workers.Remove(id);
            }
            foreach (var (id, w) in wanted)
            {
                if (_workers.ContainsKey(id)) continue;
                _workers[id] = new Worker(this, id, w.Options, w.Fingerprint);
                _log.LogInformation("Action '{Name}' ready: {Type} on {Count} kinds of event", w.Options.Name ?? id, w.Options.Type, w.Options.EffectiveEvents.Count);
            }
        }
    }

    public Task PublishAsync(UpsEvent e, CancellationToken ct)
    {
        var kind = e.Kind.ToString();
        if (ActionDefaults.Never.Contains(kind)) return Task.CompletedTask;
        var message = ActionMessage.From(e, states, options.CurrentValue.Shutdown.DryRun);
        List<Worker> workers;
        lock (_gate) workers = _workers.Values.ToList();
        foreach (var w in workers) w.Offer(e, message);
        return Task.CompletedTask;
    }

    /// <summary>Runs one action right now with the given message (the Test button). No retries.</summary>
    public async Task<ActionResult> RunOnceAsync(ActionOptions action, ActionMessage message, CancellationToken ct)
    {
        if (!_senders.TryGetValue(action.Type, out var sender)) return ActionResult.Failed($"{action.Type} isn't available in this version.", false);
        try { return await sender.SendAsync(action, message, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return ActionResult.Failed(e.Message, false); }
    }

    // ---- Failure reporting ---------------------------------------------------------------

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFailureReport = new(StringComparer.OrdinalIgnoreCase);

    private async Task ReportFailureAsync(string id, ActionOptions a, string why)
    {
        var now = _time.GetUtcNow();
        if (_lastFailureReport.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(30)) return;
        _lastFailureReport[id] = now;
        var e = new UpsEvent(UpsEvent.ThisMachine, Environment.MachineName, UpsEventKind.ActionFailed, now, null,
            $"{ActionTypes.Describe(a.Type)} \"{a.Name ?? id}\" couldn't be delivered: {why}");
        foreach (var sink in services.GetServices<IUpsEventSink>().Where(s => !ReferenceEquals(s, this)))
        {
            try { await sink.PublishAsync(e, _stopping); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Couldn't report the failed action"); }
        }
    }

    // ---- One action ----------------------------------------------------------------------

    private sealed class Worker : IDisposable
    {
        private const int MaxPerWindow = 20;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        private readonly ActionDispatcher _owner;
        private readonly string _id;
        private readonly ActionOptions _a;
        private readonly HashSet<string> _events;
        private readonly HashSet<string> _ups;
        private readonly EventHold _hold;
        private readonly Channel<ActionMessage> _queue = Channel.CreateBounded<ActionMessage>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        private readonly ConcurrentDictionary<long, ActionMessage> _pending = new();
        private readonly Queue<DateTimeOffset> _sent = new();
        private readonly CancellationTokenSource _stop;
        private long _seq;
        private bool _floodWarned;

        public string Fingerprint { get; }

        public Worker(ActionDispatcher owner, string id, ActionOptions a, string fingerprint)
        {
            _owner = owner;
            _id = id;
            _a = a;
            Fingerprint = fingerprint;
            _events = new HashSet<string>(a.EffectiveEvents, StringComparer.Ordinal);
            _ups = new HashSet<string>(a.Ups, StringComparer.OrdinalIgnoreCase);
            _hold = new EventHold(TimeSpan.FromSeconds(a.DelaySeconds), owner._time);
            _hold.Release += e =>
            {
                if (_pending.TryRemove(e.Sequence, out var m)) _queue.Writer.TryWrite(m);
            };
            _stop = CancellationTokenSource.CreateLinkedTokenSource(owner._stopping);
            _ = Task.Run(RunAsync);
        }

        private string Name => _a.Name ?? _id;

        public void Offer(UpsEvent e, ActionMessage m)
        {
            var kind = e.Kind.ToString();
            // "Run before shutdown" scripts are run (and waited for) by the shutdown itself.
            if (kind == "ShutdownInitiated" && _a.Script is { RunBeforeShutdown: true }) return;
            // The "all clear" for a problem we're watching also passes, so a held problem can be dropped.
            var watched = _events.Contains(kind) || EventHold.Pairs.Any(p => p.Value == kind && _events.Contains(p.Key));
            if (!watched) return;
            if (e.EndpointId != UpsEvent.ThisMachine && _ups.Count > 0 && !_ups.Contains(e.EndpointId)) return;

            var seq = Interlocked.Increment(ref _seq);
            // Only queue the all-clear if it was itself chosen; otherwise it just cancels a hold.
            if (_events.Contains(kind)) _pending[seq] = m;
            _hold.Handle(new ServiceEvent(seq, e.EndpointId, e.DisplayName, kind, e.Time, e.Status?.Raw, e.Message, e.IsProblem));
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var m in _queue.Reader.ReadAllAsync(_stop.Token))
                {
                    if (!WithinFloodLimit()) continue;
                    await DeliverAsync(m);
                }
            }
            catch (OperationCanceledException) { }
        }

        private bool WithinFloodLimit()
        {
            var now = _owner._time.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek() > Window) _sent.Dequeue();
            if (_sent.Count >= MaxPerWindow)
            {
                if (!_floodWarned) _owner._log.LogWarning("Action '{Name}': more than {Max} in {Minutes} minutes; skipping until it calms down.", Name, MaxPerWindow, Window.TotalMinutes);
                _floodWarned = true;
                return false;
            }
            _floodWarned = false;
            _sent.Enqueue(now);
            return true;
        }

        private async Task DeliverAsync(ActionMessage m)
        {
            var sender = _owner._senders[_a.Type];
            ActionResult result = ActionResult.Failed("not run", false);
            for (var attempt = 0; ; attempt++)
            {
                try { result = await sender.SendAsync(_a, m, _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception e) { result = ActionResult.Failed(e.Message, retry: true); }

                if (result.Ok)
                {
                    _owner._log.LogInformation("Action '{Name}' ({Type}) for {Kind}: {Result}", Name, _a.Type, m.Kind, result.Message);
                    return;
                }
                if (!result.Retry || attempt >= _owner.Retries.Length) break;
                _owner._log.LogWarning("Action '{Name}' for {Kind} failed, retrying in {Delay}: {Result}", Name, m.Kind, _owner.Retries[attempt], result.Message);
                try { await Task.Delay(_owner.Retries[attempt], _owner._time, _stop.Token); }
                catch (OperationCanceledException) { return; }
            }
            _owner._log.LogError("Action '{Name}' ({Type}) for {Kind} failed: {Result}", Name, _a.Type, m.Kind, result.Message);
            await _owner.ReportFailureAsync(_id, _a, result.Message);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _hold.Dispose();
            _queue.Writer.TryComplete();
        }
    }
}
