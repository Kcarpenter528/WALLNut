namespace WallNut.Shared.History;

/// <summary>
/// One time bucket. T is the bucket start in Unix seconds. For raw readings Min = Max = Avg;
/// for condensed history they show the range within the bucket.
/// </summary>
public sealed record HistoryPoint(long T, double Avg, double Min, double Max);

public sealed record HistorySeries(string Metric, string Label, string Unit, string Chart, IReadOnlyList<HistoryPoint> Points);

/// <summary>Readings for one UPS over a time range, at <see cref="ResolutionSeconds"/> per point.</summary>
public sealed record HistoryResult(
    string EndpointId,
    DateTimeOffset From,
    DateTimeOffset To,
    int ResolutionSeconds,
    IReadOnlyList<HistorySeries> Series);

/// <summary>A metric that has history, and from when to when.</summary>
public sealed record MetricAvailability(string Metric, string Label, string Unit, string Chart, DateTimeOffset First, DateTimeOffset Last);

/// <summary>One power failure, from going on battery to power returning (End is null while it's still going).</summary>
public sealed record Outage(
    string EndpointId,
    string DisplayName,
    DateTimeOffset Start,
    DateTimeOffset? End,
    double DurationSeconds,
    double? StartCharge,
    double? LowestCharge,
    bool ShutdownTriggered,
    string EndReason);

public sealed record OutageSummary(int Count, double TotalSeconds, double LongestSeconds, IReadOnlyList<Outage> Outages);

/// <summary>A UPS that has history (it may no longer be configured).</summary>
public sealed record HistoryEndpoint(string EndpointId, string DisplayName, DateTimeOffset First, DateTimeOffset Last);
