namespace WallNut.Shared.Actions;

/// <summary>
/// "Only if it lasts": holds the start of a problem (power failure, lost contact, overload...) for a
/// while. If the matching all-clear arrives first, both are dropped. Anything else from the same UPS
/// releases what's held first, so events always come out in order.
/// </summary>
public sealed class EventHold : IDisposable
{
    /// <summary>Problem start -> the event that clears it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Pairs = new Dictionary<string, string>
    {
        ["OnBattery"] = "OnLine",
        ["CommunicationLost"] = "CommunicationRestored",
        ["DataStale"] = "DataRestored",
        ["Overload"] = "OverloadCleared",
        ["Bypass"] = "NotBypass",
        ["Off"] = "NotOff",
        ["Alarm"] = "AlarmCleared",
    };

    private static readonly Dictionary<string, string> Clears = Pairs.ToDictionary(kv => kv.Value, kv => kv.Key);

    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<(string Endpoint, string Kind), (ServiceEvent Event, ITimer Timer)> _held = [];

    /// <summary>Raised for each event to act on, in order. May be raised from a timer thread.</summary>
    public event Action<ServiceEvent>? Release;

    public EventHold(TimeSpan delay, TimeProvider? time = null)
    {
        _delay = delay;
        _time = time ?? TimeProvider.System;
    }

    public void Handle(ServiceEvent e)
    {
        var release = new List<ServiceEvent>();
        lock (_gate)
        {
            if (_delay > TimeSpan.Zero && Pairs.ContainsKey(e.Kind))
            {
                var key = (e.EndpointId, e.Kind);
                if (_held.Remove(key, out var old)) old.Timer.Dispose(); // a repeat restarts the wait
                var timer = _time.CreateTimer(_ => Expire(key), null, _delay, Timeout.InfiniteTimeSpan);
                _held[key] = (e, timer);
                return;
            }
            if (Clears.TryGetValue(e.Kind, out var start) && _held.Remove((e.EndpointId, start), out var held))
            {
                held.Timer.Dispose(); // it cleared in time: nothing to say about either
                return;
            }
            foreach (var key in _held.Keys.Where(k => k.Endpoint == e.EndpointId).ToList())
            {
                _held.Remove(key, out var h);
                h.Timer.Dispose();
                release.Add(h.Event);
            }
            release.Add(e);
        }
        foreach (var r in release) Release?.Invoke(r);
    }

    private void Expire((string Endpoint, string Kind) key)
    {
        ServiceEvent? e = null;
        lock (_gate)
        {
            if (_held.Remove(key, out var h)) { h.Timer.Dispose(); e = h.Event; }
        }
        if (e is not null) Release?.Invoke(e);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, t) in _held.Values) t.Dispose();
            _held.Clear();
        }
    }
}
