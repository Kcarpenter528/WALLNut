using ScottPlot;
using WallNut.Shared.History;

namespace WallNut.Charts;

/// <summary>Light or dark chart colors, matching the app's theme.</summary>
public sealed record ChartTheme(bool Dark)
{
    // Matches the app's card colors so charts sit flush in their cards.
    public Color Figure => Dark ? Color.FromHex("#202020") : Color.FromHex("#FFFFFF");
    public Color Data => Dark ? Color.FromHex("#252527") : Color.FromHex("#FAFAFA");
    public Color Text => Dark ? Color.FromHex("#D6D6D6") : Color.FromHex("#3A3A3A");
    public Color Grid => Dark ? Color.FromHex("#363636") : Color.FromHex("#E8E8E8");
    public Color Outage => Color.FromHex("#E5484D").WithAlpha(Dark ? 0.30 : 0.20);
}

/// <summary>
/// Builds the history charts with ScottPlot. Pure drawing code: the app shows the result in
/// interactive controls, and tests render the same charts to images.
/// </summary>
public static class HistoryCharts
{
    // Fixed colors for the readings people look at most, so charts look the same every time.
    private static readonly Dictionary<string, string> Colors = new(StringComparer.Ordinal)
    {
        ["battery.charge"] = "#30A46C",
        ["battery.runtime"] = "#3E63DD",
        ["battery.voltage"] = "#8E4EC6",
        ["battery.current"] = "#F76B15",
        ["ups.load"] = "#F5A524",
        ["ups.realpower"] = "#8E4EC6",
        ["ups.power"] = "#3E63DD",
        ["ups.efficiency"] = "#12A594",
        ["input.voltage"] = "#12A594",
        ["input.frequency"] = "#8D8D8D",
        ["input.current"] = "#F76B15",
        ["input.realpower"] = "#12A594",
        ["output.voltage"] = "#3E63DD",
        ["output.frequency"] = "#D6409F",
        ["output.current"] = "#F76B15",
        ["output.realpower"] = "#F76B15",
        ["ups.temperature"] = "#E5484D",
        ["battery.temperature"] = "#F5A524",
        ["ambient.temperature"] = "#3E63DD",
        ["ambient.humidity"] = "#12A594",
    };
    private static readonly string[] Fallback = ["#3E63DD", "#30A46C", "#F5A524", "#E5484D", "#8E4EC6", "#12A594", "#F76B15", "#D6409F", "#8D8D8D"];

    /// <summary>The charts that have data in this result, in display order.</summary>
    public static IReadOnlyList<MetricChart> ChartsWithData(HistoryResult result) =>
        result.Series
            .Where(s => s.Points.Count > 0 && Enum.TryParse<MetricChart>(s.Chart, out var c) && c != MetricChart.Status)
            .Select(s => Enum.Parse<MetricChart>(s.Chart))
            .Distinct()
            .Order()
            .ToList();

    /// <summary>X coordinate for a time: local time, as ScottPlot's date axis expects.</summary>
    public static double X(DateTimeOffset t) => t.ToLocalTime().DateTime.ToOADate();
    public static double X(long unixSeconds) => X(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    /// <summary>Draws one chart (e.g. Battery) for the range, with outages shaded.</summary>
    public static void Build(Plot plot, HistoryResult result, MetricChart chart, IReadOnlyList<Outage> outages, ChartTheme theme)
    {
        plot.Clear();
        ApplyTheme(plot, theme);
        plot.Axes.DateTimeTicksBottom();
        plot.Title(MetricCatalog.ChartTitle(chart), size: 14);

        var series = result.Series
            .Where(s => s.Chart == chart.ToString() && s.Points.Count > 0)
            .OrderBy(s => MetricCatalog.Order(s.Metric)).ThenBy(s => s.Metric, StringComparer.Ordinal)
            .ToList();

        // Outages first so the lines draw on top.
        var shaded = false;
        foreach (var o in outages)
        {
            var start = Math.Max(X(o.Start), X(result.From));
            var end = Math.Min(X(o.End ?? result.To), X(result.To));
            if (end <= start) continue;
            var span = plot.Add.HorizontalSpan(start, end);
            span.FillColor = theme.Outage;
            span.LineColor = Colors_.Transparent;
            if (!shaded) { span.LegendText = "Power outage"; shaded = true; }
        }

        // Up to two units: the first on the left axis, the second on the right. (MetricCatalog
        // groups metrics so no chart has more; anything beyond two is left out rather than misdrawn.)
        var units = series.Select(s => DisplayUnit(s)).Distinct().Take(2).ToList();
        series = series.Where(s => units.Contains(DisplayUnit(s))).ToList();
        // Known readings keep their own color; the rest take palette colors not already on this chart.
        var used = series.Where(s => Colors.ContainsKey(s.Metric)).Select(s => Colors[s.Metric]).ToHashSet();
        var spare = new Queue<string>(Fallback.Where(c => !used.Contains(c)).Concat(Fallback));
        foreach (var s in series)
        {
            var unit = DisplayUnit(s);
            var right = units.Count > 1 && unit == units[1];
            var color = Color.FromHex(Colors.TryGetValue(s.Metric, out var hex) ? hex : spare.Dequeue());
            AddSeries(plot, s, result.ResolutionSeconds, color, right ? plot.Axes.Right : plot.Axes.Left, theme);
        }

        plot.Axes.Left.Label.Text = units.Count > 0 ? units[0] : "";
        plot.Axes.Right.Label.Text = units.Count > 1 ? units[1] : "";
        plot.Axes.SetLimitsX(X(result.From), X(result.To));
        plot.Axes.AutoScaleY(plot.Axes.Left);
        if (units.Count > 1) plot.Axes.AutoScaleY(plot.Axes.Right);

        // Percent scales read best from 0 to 100.
        if (units.Count > 0 && units[0] == "%")
        {
            var top = Math.Max(100, plot.Axes.GetLimits(plot.Axes.Bottom, plot.Axes.Left).Top);
            plot.Axes.SetLimitsY(0, top * 1.02, plot.Axes.Left);
        }
        if (units.Count > 1 && units[1] == "min")
        {
            var top = plot.Axes.GetLimits(plot.Axes.Bottom, plot.Axes.Right).Top;
            plot.Axes.SetLimitsY(0, Math.Max(1, top * 1.08), plot.Axes.Right);
        }

        // Legend in one row above the data, so it never hides a line.
        plot.ShowLegend(Edge.Top);
        plot.Legend.Orientation = Orientation.Horizontal;
        plot.Legend.BackgroundColor = theme.Figure;
        plot.Legend.FontColor = theme.Text;
        plot.Legend.OutlineColor = theme.Grid;
    }

    /// <summary>
    /// Values at a moment, for the hover readout: each series' bucket covering <paramref name="when"/>
    /// (or nothing if there's no data that close).
    /// </summary>
    public static IEnumerable<(HistorySeries Series, double Value)> ValuesAt(HistoryResult result, DateTimeOffset when)
    {
        var t = when.ToUnixTimeSeconds();
        foreach (var s in result.Series.OrderBy(s => MetricCatalog.Order(s.Metric)))
        {
            if (s.Points.Count == 0) continue;
            // Binary search for the last bucket starting at or before t.
            int lo = 0, hi = s.Points.Count - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (s.Points[mid].T <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found >= 0 && t - s.Points[found].T <= result.ResolutionSeconds * 2.5)
                yield return (s, DisplayValue(s, s.Points[found].Avg));
        }
    }

    /// <summary>Unit shown on the axis (runtime is shown in minutes).</summary>
    public static string DisplayUnit(HistorySeries s) => s.Metric == "battery.runtime" ? "min" : s.Unit;

    /// <summary>Value shown on the chart (runtime seconds become minutes).</summary>
    public static double DisplayValue(HistorySeries s, double v) => s.Metric == "battery.runtime" ? v / 60 : v;

    private static void AddSeries(Plot plot, HistorySeries s, int resolution, Color color, IYAxis axis, ChartTheme theme)
    {
        // Gaps: when there's no data for a while (UPS unreachable, service stopped), break the line.
        var gap = Math.Max(resolution * 2.5, 45);
        var segments = new List<List<HistoryPoint>> { new() };
        for (var i = 0; i < s.Points.Count; i++)
        {
            if (i > 0 && s.Points[i].T - s.Points[i - 1].T > gap) segments.Add([]);
            segments[^1].Add(s.Points[i]);
        }

        var showBand = s.Points.Any(p => p.Max - p.Min > 1e-9);
        var legend = true;
        foreach (var seg in segments.Where(g => g.Count > 0))
        {
            // Bucket start plus half a bucket puts each point in the middle of the time it covers.
            var xs = seg.Select(p => X(p.T + resolution / 2)).ToArray();
            var avg = seg.Select(p => DisplayValue(s, p.Avg)).ToArray();

            if (showBand)
            {
                var band = plot.Add.FillY(xs, seg.Select(p => DisplayValue(s, p.Min)).ToArray(), seg.Select(p => DisplayValue(s, p.Max)).ToArray());
                band.FillColor = color.WithAlpha(theme.Dark ? 0.22 : 0.16);
                band.LineColor = Colors_.Transparent;
                band.Axes.YAxis = axis;
            }

            var line = seg.Count == 1 ? plot.Add.Scatter(xs, avg) : plot.Add.ScatterLine(xs, avg);
            line.Color = color;
            line.LineWidth = 2;
            line.MarkerSize = seg.Count == 1 ? 5 : 0;
            line.Axes.YAxis = axis;
            if (legend) { line.LegendText = $"{s.Label} ({DisplayUnit(s)})"; legend = false; }
        }
    }

    private static void ApplyTheme(Plot plot, ChartTheme theme)
    {
        plot.FigureBackground.Color = theme.Figure;
        plot.DataBackground.Color = theme.Data;
        plot.Axes.Color(theme.Text);
        plot.Grid.MajorLineColor = theme.Grid;
        plot.Axes.Title.Label.ForeColor = theme.Text;
        // A light, modern frame: keep the scales, drop the box.
        foreach (var axis in new IAxis[] { plot.Axes.Left, plot.Axes.Right, plot.Axes.Bottom, plot.Axes.Top })
            axis.FrameLineStyle.Width = 0;
        plot.Grid.XAxisStyle.MajorLineStyle.Width = 1;
        plot.Grid.YAxisStyle.MajorLineStyle.Width = 1;
    }

    private static class Colors_
    {
        public static readonly Color Transparent = Color.FromARGB(0u);
    }
}
