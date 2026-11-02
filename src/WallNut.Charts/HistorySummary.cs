using WallNut.Shared.History;

namespace WallNut.Charts;

/// <summary>The headline numbers for a time range (the cards above the charts).</summary>
public sealed record HistorySummary(
    int Outages,
    TimeSpan TimeOnBattery,
    TimeSpan LongestOutage,
    double? LowestCharge,
    double? AverageLoad,
    double? PeakLoad)
{
    public static HistorySummary From(HistoryResult result, OutageSummary outages)
    {
        HistorySeries? Find(string metric) => result.Series.FirstOrDefault(s => s.Metric == metric && s.Points.Count > 0);
        var charge = Find("battery.charge");
        var load = Find("ups.load");
        return new HistorySummary(
            outages.Count,
            TimeSpan.FromSeconds(outages.TotalSeconds),
            TimeSpan.FromSeconds(outages.LongestSeconds),
            charge?.Points.Min(p => p.Min),
            load?.Points.Average(p => p.Avg),
            load?.Points.Max(p => p.Max));
    }

    /// <summary>"3 h 12 min", "45 min", "40 s".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min"
        : $"{(int)t.TotalSeconds} s";
}
