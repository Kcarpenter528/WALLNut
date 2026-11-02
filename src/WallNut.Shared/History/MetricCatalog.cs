using System.Text.RegularExpressions;

namespace WallNut.Shared.History;

/// <summary>Which chart a metric belongs on. Every chart has at most two units (left and right axis).</summary>
public enum MetricChart
{
    Battery, BatteryElectrical, Load, Power, Voltage, Current, Temperature, Environment,
    PhaseVoltage, PhaseCurrent, PhasePower, Outlets, Status,
}

/// <summary>A recorded number: the NUT variable it comes from, a label and a unit for charts.</summary>
public sealed record MetricInfo(string Metric, string Label, string Unit, MetricChart Chart);

/// <summary>
/// The NUT variables WALLNut records over time. Only values that change and are worth charting
/// are kept (not nominal ratings, thresholds or text), so history stays small.
/// </summary>
public static partial class MetricCatalog
{
    /// <summary>1 while on battery, 0 on line power. Recorded by WALLNut itself, from ups.status.</summary>
    public const string OnBattery = "wallnut.on_battery";

    private static readonly Dictionary<string, MetricInfo> Fixed = new MetricInfo[]
    {
        new(OnBattery, "On battery", "", MetricChart.Status),
        new("battery.charge", "Battery charge", "%", MetricChart.Battery),
        new("battery.runtime", "Runtime left", "s", MetricChart.Battery),
        new("battery.voltage", "Battery voltage", "V", MetricChart.BatteryElectrical),
        new("battery.current", "Battery current", "A", MetricChart.BatteryElectrical),
        new("ups.load", "Load", "%", MetricChart.Load),
        new("ups.efficiency", "Efficiency", "%", MetricChart.Load),
        new("ups.realpower", "Real power", "W", MetricChart.Power),
        new("ups.power", "Apparent power", "VA", MetricChart.Power),
        new("input.realpower", "Input power", "W", MetricChart.Power),
        new("output.realpower", "Output power", "W", MetricChart.Power),
        new("input.voltage", "Input voltage", "V", MetricChart.Voltage),
        new("output.voltage", "Output voltage", "V", MetricChart.Voltage),
        new("input.frequency", "Input frequency", "Hz", MetricChart.Voltage),
        new("output.frequency", "Output frequency", "Hz", MetricChart.Voltage),
        new("input.current", "Input current", "A", MetricChart.Current),
        new("output.current", "Output current", "A", MetricChart.Current),
        new("ups.temperature", "UPS", "°C", MetricChart.Temperature),
        new("battery.temperature", "Battery", "°C", MetricChart.Temperature),
        new("ambient.temperature", "Ambient", "°C", MetricChart.Temperature),
        new("ambient.humidity", "Humidity", "%", MetricChart.Environment),
    }.ToDictionary(m => m.Metric, StringComparer.Ordinal);

    [GeneratedRegex(@"^(input|output)\.(L[123](?:-N|-L[123])?)\.voltage$")] private static partial Regex PhaseVoltage();
    [GeneratedRegex(@"^(input|output)\.(L[123]|N)\.current$")] private static partial Regex PhaseCurrent();
    [GeneratedRegex(@"^output\.(L[123])\.(power|realpower|power\.percent)$")] private static partial Regex PhasePower();
    [GeneratedRegex(@"^ambient\.(\d+)\.(temperature|humidity)$")] private static partial Regex Probe();
    [GeneratedRegex(@"^outlet\.(\d+)\.(current|realpower)$")] private static partial Regex Outlet();

    /// <summary>The metric's description, or null if WALLNut doesn't record it.</summary>
    public static MetricInfo? Describe(string metric)
    {
        if (Fixed.TryGetValue(metric, out var m)) return m;
        Match x;
        if ((x = PhaseVoltage().Match(metric)).Success)
            return new(metric, $"{Cap(x.Groups[1].Value)} {x.Groups[2].Value}", "V", MetricChart.PhaseVoltage);
        if ((x = PhaseCurrent().Match(metric)).Success)
            return new(metric, $"{Cap(x.Groups[1].Value)} {x.Groups[2].Value} current", "A", MetricChart.PhaseCurrent);
        if ((x = PhasePower().Match(metric)).Success)
            return x.Groups[2].Value switch
            {
                "power" => new(metric, $"Output {x.Groups[1].Value} power", "VA", MetricChart.PhasePower),
                "realpower" => new(metric, $"Output {x.Groups[1].Value} real power", "W", MetricChart.PhasePower),
                _ => new(metric, $"Output {x.Groups[1].Value} load", "%", MetricChart.Load),
            };
        if ((x = Probe().Match(metric)).Success)
            return x.Groups[2].Value == "temperature"
                ? new(metric, $"Probe {x.Groups[1].Value}", "°C", MetricChart.Temperature)
                : new(metric, $"Probe {x.Groups[1].Value} humidity", "%", MetricChart.Environment);
        if ((x = Outlet().Match(metric)).Success)
            return new(metric, $"Outlet {x.Groups[1].Value} {(x.Groups[2].Value == "current" ? "current" : "power")}",
                x.Groups[2].Value == "current" ? "A" : "W", MetricChart.Outlets);
        return null;
    }

    public static bool IsTracked(string metric) => Describe(metric) is not null;

    private static readonly Dictionary<string, int> Rank = Fixed.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

    /// <summary>Display order within a chart: the catalog's order first (so the main reading gets the left axis), then by name.</summary>
    public static int Order(string metric) => Rank.TryGetValue(metric, out var i) ? i : 1000;

    public static string ChartTitle(MetricChart c) => c switch
    {
        MetricChart.Battery => "Battery",
        MetricChart.BatteryElectrical => "Battery voltage and current",
        MetricChart.Load => "Load",
        MetricChart.Power => "Power",
        MetricChart.Voltage => "Voltage and frequency",
        MetricChart.Current => "Current",
        MetricChart.Temperature => "Temperatures",
        MetricChart.Environment => "Humidity",
        MetricChart.PhaseVoltage => "Per-phase voltage",
        MetricChart.PhaseCurrent => "Per-phase current",
        MetricChart.PhasePower => "Per-phase power",
        MetricChart.Outlets => "Outlets",
        _ => "Status",
    };

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
