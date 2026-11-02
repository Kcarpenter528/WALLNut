using System.Globalization;
using WallNut.Shared.History;

namespace WallNut.History;

/// <summary>Picks the numbers worth recording out of a UPS's NUT variables.</summary>
public static class MetricExtractor
{
    public static IEnumerable<KeyValuePair<string, double>> Extract(IReadOnlyDictionary<string, string> vars)
    {
        foreach (var (name, value) in vars)
        {
            if (MetricCatalog.IsTracked(name)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                yield return new(name, number);
        }
        if (vars.TryGetValue("ups.status", out var status))
        {
            var onBattery = status.Split(' ').Contains("OB");
            yield return new(MetricCatalog.OnBattery, onBattery ? 1 : 0);
        }
    }
}
