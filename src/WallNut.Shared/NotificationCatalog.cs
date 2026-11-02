namespace WallNut.Shared;

public enum NotificationGroup { Power, Battery, Shutdown, Connection, Warnings, Informational }

public enum NotificationUrgency { Low, Normal, Critical }

/// <summary>How one kind of event is presented as a notification.</summary>
public sealed record NotificationKind(
    string Kind,
    NotificationGroup Group,
    string Title,
    NotificationUrgency Urgency,
    bool OnByDefault = true,
    /// <summary>Can't be switched off: the user must know their machine is about to go down.</summary>
    bool AlwaysShown = false);

/// <summary>Every event kind the service can send, grouped for the notification settings screen.</summary>
public static class NotificationCatalog
{
    public static IReadOnlyList<NotificationKind> All { get; } =
    [
        new("OnBattery", NotificationGroup.Power, "Power failure", NotificationUrgency.Critical),
        new("OnLine", NotificationGroup.Power, "Power restored", NotificationUrgency.Normal),

        new("LowBattery", NotificationGroup.Battery, "Battery low", NotificationUrgency.Critical),
        new("ReplaceBattery", NotificationGroup.Battery, "Replace battery", NotificationUrgency.Normal),
        new("ReplaceBatteryCleared", NotificationGroup.Battery, "Battery OK", NotificationUrgency.Low, OnByDefault: false),

        new("ShutdownInitiated", NotificationGroup.Shutdown, "Shutting down", NotificationUrgency.Critical, AlwaysShown: true),
        new("ShutdownFailed", NotificationGroup.Shutdown, "Shutdown failed", NotificationUrgency.Critical, AlwaysShown: true),
        new("ForcedShutdown", NotificationGroup.Shutdown, "Forced shutdown", NotificationUrgency.Critical, AlwaysShown: true),
        new("ShutdownRearmed", NotificationGroup.Shutdown, "Shutdown test reset", NotificationUrgency.Low, OnByDefault: false),

        new("CommunicationLost", NotificationGroup.Connection, "Lost contact with UPS", NotificationUrgency.Normal),
        new("CommunicationRestored", NotificationGroup.Connection, "UPS contact restored", NotificationUrgency.Low),
        new("DataStale", NotificationGroup.Connection, "UPS not reporting", NotificationUrgency.Normal),
        new("DataRestored", NotificationGroup.Connection, "UPS reporting again", NotificationUrgency.Low),

        new("Overload", NotificationGroup.Warnings, "UPS overloaded", NotificationUrgency.Critical),
        new("OverloadCleared", NotificationGroup.Warnings, "Overload cleared", NotificationUrgency.Low),
        new("Bypass", NotificationGroup.Warnings, "UPS on bypass", NotificationUrgency.Normal),
        new("NotBypass", NotificationGroup.Warnings, "UPS off bypass", NotificationUrgency.Low),
        new("Off", NotificationGroup.Warnings, "UPS outlets off", NotificationUrgency.Critical),
        new("NotOff", NotificationGroup.Warnings, "UPS outlets on", NotificationUrgency.Low),
        new("Alarm", NotificationGroup.Warnings, "UPS alarm", NotificationUrgency.Normal),
        new("AlarmCleared", NotificationGroup.Warnings, "UPS alarm cleared", NotificationUrgency.Low),

        new("Connected", NotificationGroup.Informational, "Connected to UPS", NotificationUrgency.Low, OnByDefault: false),
        new("Calibrating", NotificationGroup.Informational, "Battery calibration", NotificationUrgency.Low, OnByDefault: false),
        new("NotCalibrating", NotificationGroup.Informational, "Calibration finished", NotificationUrgency.Low, OnByDefault: false),
        new("Test", NotificationGroup.Informational, "Test notification", NotificationUrgency.Normal, AlwaysShown: true),
    ];

    private static readonly Dictionary<string, NotificationKind> ByKind = All.ToDictionary(k => k.Kind, StringComparer.Ordinal);

    /// <summary>Known kinds come from the table; anything newer still shows, as a normal notification.</summary>
    public static NotificationKind Find(string kind) =>
        ByKind.TryGetValue(kind, out var k) ? k : new NotificationKind(kind, NotificationGroup.Informational, kind, NotificationUrgency.Normal);

    public static string Describe(NotificationGroup g) => g switch
    {
        NotificationGroup.Power => "Power failures and power returning",
        NotificationGroup.Battery => "Low battery and battery health",
        NotificationGroup.Shutdown => "This computer shutting down (always shown)",
        NotificationGroup.Connection => "Losing or regaining contact with a UPS",
        NotificationGroup.Warnings => "Overload, bypass, outlets off and other UPS alarms",
        NotificationGroup.Informational => "Connections, calibration and other routine events",
        _ => g.ToString(),
    };
}
