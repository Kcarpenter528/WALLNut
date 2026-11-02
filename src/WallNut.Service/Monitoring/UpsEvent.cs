using WallNut.Nut;

namespace WallNut.Service.Monitoring;

/// <summary>
/// Things that can happen to a UPS. Names follow NUT upsmon's notification types where one exists
/// (ONBATT, ONLINE, LOWBATT, FSD, COMMBAD, COMMOK, REPLBATT, OVER, OFF, BYPASS, CAL, ALARM...).
/// </summary>
public enum UpsEventKind
{
    /// <summary>First successful connection since the service started.</summary>
    Connected,
    /// <summary>COMMBAD: lost contact with the NUT server (or never reached it).</summary>
    CommunicationLost,
    /// <summary>COMMOK: contact is back.</summary>
    CommunicationRestored,
    /// <summary>The server is reachable but its driver lost the UPS (ERR DATA-STALE).</summary>
    DataStale,
    DataRestored,

    /// <summary>ONBATT: mains power is gone, running on battery.</summary>
    OnBattery,
    /// <summary>ONLINE: mains power is back.</summary>
    OnLine,
    /// <summary>LOWBATT: on battery and nearly empty.</summary>
    LowBattery,
    /// <summary>FSD: the NUT primary (e.g. the NAS) is forcing a shutdown.</summary>
    ForcedShutdown,
    /// <summary>REPLBATT: the battery needs replacing.</summary>
    ReplaceBattery,
    ReplaceBatteryCleared,
    Overload,
    OverloadCleared,
    /// <summary>OFF: the UPS is not powering its outlets.</summary>
    Off,
    NotOff,
    Bypass,
    NotBypass,
    Calibrating,
    NotCalibrating,
    Alarm,
    AlarmCleared,

    /// <summary>SHUTDOWN: this machine is shutting down (or would, in test mode). Not tied to one UPS.</summary>
    ShutdownInitiated,
    /// <summary>Test mode only: the condition cleared, so the next one will be reported again.</summary>
    ShutdownRearmed,
    /// <summary>The shutdown command failed. WALLNut keeps retrying.</summary>
    ShutdownFailed,
}

/// <summary>Something that happened to one UPS endpoint.</summary>
public sealed record UpsEvent(
    string EndpointId,
    string DisplayName,
    UpsEventKind Kind,
    DateTimeOffset Time,
    UpsStatus? Status,
    string Message)
{
    /// <summary>Events that need someone's attention (or may lead to a shutdown).</summary>
    public bool IsProblem => Kind is UpsEventKind.CommunicationLost or UpsEventKind.DataStale
        or UpsEventKind.OnBattery or UpsEventKind.LowBattery or UpsEventKind.ForcedShutdown
        or UpsEventKind.ReplaceBattery or UpsEventKind.Overload or UpsEventKind.Off
        or UpsEventKind.Bypass or UpsEventKind.Alarm or UpsEventKind.ShutdownInitiated or UpsEventKind.ShutdownFailed;

    /// <summary>The endpoint id used for events about this machine rather than one UPS.</summary>
    public const string ThisMachine = "this-machine";
}
