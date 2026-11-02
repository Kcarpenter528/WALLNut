namespace WallNut.Service.Monitoring;

/// <summary>
/// Wait times between reconnect attempts: starts short, doubles each failure, stops growing at
/// a maximum, and never gives up. A little randomness keeps many clients from retrying in lockstep.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly Random _random;
    private TimeSpan _next;

    public ReconnectBackoff(TimeSpan initial, TimeSpan max, Random? random = null)
    {
        _initial = initial;
        _max = max;
        _random = random ?? Random.Shared;
        _next = initial;
    }

    public int Failures { get; private set; }

    /// <summary>How long to wait before the next attempt.</summary>
    public TimeSpan Next()
    {
        Failures++;
        var delay = _next;
        _next = TimeSpan.FromTicks(Math.Min(_max.Ticks, _next.Ticks * 2));
        // +/- 10%, never above the max.
        var jittered = delay * (0.9 + _random.NextDouble() * 0.2);
        return jittered > _max ? _max : jittered;
    }

    /// <summary>Call after a successful connection.</summary>
    public void Reset()
    {
        _next = _initial;
        Failures = 0;
    }
}
