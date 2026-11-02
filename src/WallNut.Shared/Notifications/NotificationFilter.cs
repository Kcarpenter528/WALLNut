namespace WallNut.Shared.Notifications;

/// <summary>
/// Decides which incoming events become notifications, applying the person's preferences and
/// holding "power failure" briefly so that power blips don't pop anything up.
/// </summary>
public sealed class NotificationFilter : IDisposable
{
    private readonly Func<NotificationPreferences> _preferences;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ServiceEvent Event, ITimer Timer)> _heldPowerFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for each event that should be shown, in order. May be raised from a timer thread.</summary>
    public event Action<ServiceEvent>? Show;

    public NotificationFilter(Func<NotificationPreferences> preferences, TimeProvider? time = null)
    {
        _preferences = preferences;
        _time = time ?? TimeProvider.System;
    }

    public void Handle(ServiceEvent e)
    {
        var prefs = _preferences();
        var show = new List<ServiceEvent>();
        lock (_gate)
        {
            if (e.Kind == "OnBattery" && prefs.OnBatteryDelaySeconds > 0)
            {
                Release(e.EndpointId, show);
                var timer = _time.CreateTimer(_ => OnHoldExpired(e.EndpointId), null,
                    TimeSpan.FromSeconds(prefs.OnBatteryDelaySeconds), Timeout.InfiniteTimeSpan);
                _heldPowerFailures[e.EndpointId] = (e, timer);
            }
            else if (e.Kind == "OnLine" && _heldPowerFailures.Remove(e.EndpointId, out var held))
            {
                // A blip: power came back before we said it went. Say nothing about either.
                held.Timer.Dispose();
            }
            else
            {
                // Anything else from this UPS (low battery, lost contact...) means the outage is real.
                Release(e.EndpointId, show);
                show.Add(e);
            }
        }
        Emit(show, prefs);
    }

    private void OnHoldExpired(string endpointId)
    {
        var show = new List<ServiceEvent>();
        lock (_gate) Release(endpointId, show);
        Emit(show, _preferences());
    }

    private void Release(string endpointId, List<ServiceEvent> into)
    {
        if (_heldPowerFailures.Remove(endpointId, out var held))
        {
            held.Timer.Dispose();
            into.Add(held.Event);
        }
    }

    private void Emit(List<ServiceEvent> events, NotificationPreferences prefs)
    {
        foreach (var e in events)
            if (prefs.Wants(e)) Show?.Invoke(e);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, timer) in _heldPowerFailures.Values) timer.Dispose();
            _heldPowerFailures.Clear();
        }
    }
}
