using WallNut.Nut;

namespace WallNut.Service.Monitoring;

/// <summary>Turns two consecutive ups.status readings into events.</summary>
public static class EventDetector
{
    private static readonly (UpsStatusFlags Flag, UpsEventKind Set, UpsEventKind? Cleared)[] Rules =
    [
        (UpsStatusFlags.OnBattery, UpsEventKind.OnBattery, null), // clearing is reported as OnLine below
        (UpsStatusFlags.LowBattery, UpsEventKind.LowBattery, null),
        (UpsStatusFlags.ForcedShutdown, UpsEventKind.ForcedShutdown, null),
        (UpsStatusFlags.ReplaceBattery, UpsEventKind.ReplaceBattery, UpsEventKind.ReplaceBatteryCleared),
        (UpsStatusFlags.Overloaded, UpsEventKind.Overload, UpsEventKind.OverloadCleared),
        (UpsStatusFlags.Offline, UpsEventKind.Off, UpsEventKind.NotOff),
        (UpsStatusFlags.Bypass, UpsEventKind.Bypass, UpsEventKind.NotBypass),
        (UpsStatusFlags.Calibrating, UpsEventKind.Calibrating, UpsEventKind.NotCalibrating),
        (UpsStatusFlags.Alarm, UpsEventKind.Alarm, UpsEventKind.AlarmCleared),
    ];

    /// <summary>
    /// Events implied by going from <paramref name="previous"/> to <paramref name="current"/>.
    /// With no previous reading (just connected), reports any abnormal state already in effect,
    /// so starting the service mid-outage still raises OnBattery.
    /// </summary>
    public static IReadOnlyList<UpsEventKind> Detect(UpsStatus? previous, UpsStatus current)
    {
        var before = previous?.Flags ?? UpsStatusFlags.None;
        var now = current.Flags;
        var events = new List<UpsEventKind>();

        // Power source first: it's the headline.
        bool wasOnBattery = (before & UpsStatusFlags.OnBattery) != 0;
        bool isOnBattery = (now & UpsStatusFlags.OnBattery) != 0;
        bool isOnLine = (now & UpsStatusFlags.OnLine) != 0;
        if (wasOnBattery && !isOnBattery && isOnLine) events.Add(UpsEventKind.OnLine);

        foreach (var (flag, set, cleared) in Rules)
        {
            bool had = (before & flag) != 0, has = (now & flag) != 0;
            if (has && !had) events.Add(set);
            else if (had && !has && cleared is { } c) events.Add(c);
        }
        return events;
    }
}
