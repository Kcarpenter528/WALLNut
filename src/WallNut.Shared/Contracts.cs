namespace WallNut.Shared;

/// <summary>One UPS as the service sees it.</summary>
public sealed record UpsSummary(
    string Id,
    string DisplayName,
    string Host,
    int Port,
    string Ups,
    string Role,
    bool Connected,
    bool Stale,
    string? Status,
    bool OnBattery,
    double? ChargePercent,
    double? RuntimeSeconds,
    double? LoadPercent,
    string? Manufacturer,
    string? Model,
    string? Serial,
    DateTimeOffset? LastUpdate,
    DateTimeOffset? OnBatterySince,
    string? LastError);

/// <summary>The service and its shutdown setup.</summary>
public sealed record ServiceInfo(
    string Version,
    string Machine,
    bool ShutdownEnabled,
    bool ShutdownTestMode,
    string ShutdownAction,
    bool ShutdownInitiated,
    IReadOnlyList<UpsSummary> Ups);

/// <summary>Something that happened, as streamed to apps. Kind is a <see cref="NotificationCatalog"/> name.</summary>
public sealed record ServiceEvent(
    long Sequence,
    string EndpointId,
    string DisplayName,
    string Kind,
    DateTimeOffset Time,
    string? Status,
    string Message,
    bool IsProblem);
