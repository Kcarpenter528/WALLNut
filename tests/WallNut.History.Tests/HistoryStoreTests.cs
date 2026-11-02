using WallNut.Shared;
using WallNut.Shared.History;

namespace WallNut.History.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero); // on an hour boundary
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wallnut-history-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _store;
    private long _seq;

    public HistoryStoreTests() => _store = new HistoryStore(_path, sampleSeconds: 15);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private void Sample(DateTimeOffset t, double charge, double load = 20, string endpoint = "nas") =>
        _store.AddSamples(endpoint, "Synology NAS", t, [new("battery.charge", charge), new("ups.load", load)]);

    private void Event(string kind, DateTimeOffset t, string endpoint = "nas") =>
        _store.AddEvent(new ServiceEvent(++_seq, endpoint, endpoint == "this-machine" ? "PC" : "Synology NAS", kind, t, null, kind, true));

    /// <summary>Samples every 15 s from <paramref name="start"/> for <paramref name="duration"/>, charge from f(t).</summary>
    private void Fill(DateTimeOffset start, TimeSpan duration, Func<double, double> charge)
    {
        for (var s = 0; s < duration.TotalSeconds; s += 15) Sample(start.AddSeconds(s), charge(s));
    }

    [Fact]
    public void Raw_samples_come_back_as_written()
    {
        Fill(T0, TimeSpan.FromMinutes(2), s => 100 - s / 15);
        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(2), 1000, T0.AddMinutes(2));

        Assert.Equal(15, r.ResolutionSeconds);
        var pts = r.Series.Single().Points;
        Assert.Equal(8, pts.Count);
        Assert.Equal(100, pts[0].Avg);
        Assert.Equal(93, pts[^1].Avg);
        Assert.All(pts, p => Assert.Equal(p.Avg, p.Min));
        Assert.Equal("Battery charge", r.Series[0].Label);
        Assert.Equal("%", r.Series[0].Unit);
    }

    [Fact]
    public void Long_ranges_are_bucketed_to_the_point_limit()
    {
        Fill(T0, TimeSpan.FromHours(1), s => 50 + (s % 60 == 0 ? 10 : 0));
        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddHours(1), 12, T0.AddHours(1));
        var pts = r.Series[0].Points;

        Assert.Equal(300, r.ResolutionSeconds);
        Assert.Equal(12, pts.Count);
        Assert.All(pts, p => { Assert.Equal(50, p.Min); Assert.Equal(60, p.Max); Assert.Equal(52.5, p.Avg, 3); });
    }

    [Fact]
    public void Rollups_keep_average_min_and_max()
    {
        // 20 samples in the first 5 minutes: 0, 1, ..., 19.
        for (var i = 0; i < 20; i++) Sample(T0.AddSeconds(i * 15), i);
        _store.Rollup(T0.AddMinutes(10));

        // Ask for a range old enough that raw data would be outside its retention.
        var later = T0.AddDays(10);
        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(5), 100, later);
        var p = Assert.Single(r.Series[0].Points);
        Assert.Equal(300, r.ResolutionSeconds);
        Assert.Equal(0, p.Min);
        Assert.Equal(19, p.Max);
        Assert.Equal(9.5, p.Avg, 6);
    }

    [Fact]
    public void Hourly_rollup_weights_by_sample_count()
    {
        Fill(T0, TimeSpan.FromHours(2), s => s < 3600 ? 100 : 40);
        _store.Rollup(T0.AddHours(3));

        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddHours(2), 100, T0.AddDays(200));
        Assert.Equal(3600, r.ResolutionSeconds);
        Assert.Equal([100.0, 40.0], r.Series[0].Points.Select(p => Math.Round(p.Avg, 6)));
    }

    [Fact]
    public void Recent_minutes_not_yet_condensed_are_included()
    {
        Fill(T0, TimeSpan.FromHours(1), _ => 80);
        _store.Rollup(T0.AddMinutes(50));                         // condensed up to 50 minutes
        Fill(T0.AddHours(1), TimeSpan.FromMinutes(10), _ => 30);  // newer, raw only

        // A coarse query (5-minute buckets) over the whole span must still show the newest data.
        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(70), 14, T0.AddMinutes(70));
        Assert.Equal(300, r.ResolutionSeconds);
        Assert.Equal(14, r.Series[0].Points.Count);
        Assert.Equal(30, r.Series[0].Points[^1].Avg);
        Assert.Equal(80, r.Series[0].Points[0].Avg);
    }

    [Fact]
    public void Rollup_is_safe_to_repeat()
    {
        Fill(T0, TimeSpan.FromMinutes(30), _ => 70);
        _store.Rollup(T0.AddMinutes(30));
        _store.Rollup(T0.AddMinutes(30));
        _store.Rollup(T0.AddMinutes(31));
        var r = _store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(30), 100, T0.AddDays(10));
        Assert.Equal(6, r.Series[0].Points.Count);
        Assert.All(r.Series[0].Points, p => Assert.Equal(70, p.Avg));
    }

    [Fact]
    public void Retention_removes_old_data_per_level()
    {
        Fill(T0, TimeSpan.FromMinutes(10), _ => 90);
        _store.Rollup(T0.AddHours(2));
        Event("OnBattery", T0);

        _store.ApplyRetention(T0.AddDays(4)); // raw kept 3 days
        var coarse = _store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(10), 100, T0.AddDays(4));
        Assert.Equal(2, coarse.Series[0].Points.Count); // 5-minute data survives

        _store.ApplyRetention(T0.AddDays(800));
        Assert.Empty(_store.Query("nas", ["battery.charge"], T0, T0.AddMinutes(10), 100, T0.AddDays(800)).Series[0].Points);
        Assert.Empty(_store.Events("nas", T0.AddDays(-1), T0.AddDays(1)));
    }

    [Fact]
    public void Unknown_metric_or_ups_returns_empty_series()
    {
        var r = _store.Query("nope", ["battery.charge"], T0, T0.AddHours(1), 100, T0.AddHours(1));
        Assert.Empty(r.Series.Single().Points);
    }

    [Fact]
    public void Endpoints_and_metrics_are_listed()
    {
        Fill(T0, TimeSpan.FromMinutes(1), _ => 100);
        var e = Assert.Single(_store.Endpoints());
        Assert.Equal("nas", e.EndpointId);
        Assert.Equal("Synology NAS", e.DisplayName);
        Assert.Equal(T0, e.First);

        var metrics = _store.Metrics("nas").Select(m => m.Metric).ToList();
        Assert.Equal(["battery.charge", "ups.load"], metrics);
    }

    [Fact]
    public void Events_are_returned_newest_first_and_include_machine_events()
    {
        Event("OnBattery", T0);
        Event("LowBattery", T0.AddMinutes(5));
        Event("ShutdownInitiated", T0.AddMinutes(6), "this-machine");
        Event("OnBattery", T0, "office");

        var list = _store.Events("nas", T0, T0.AddHours(1));
        Assert.Equal(["ShutdownInitiated", "LowBattery", "OnBattery"], list.Select(e => e.Kind));
    }

    [Fact]
    public void Outages_are_worked_out_from_events()
    {
        Fill(T0, TimeSpan.FromMinutes(30), s => s < 600 ? 100 : 100 - (s - 600) / 30.0);
        Event("OnBattery", T0.AddMinutes(10));
        Event("OnLine", T0.AddMinutes(25));
        Event("OnBattery", T0.AddMinutes(28));                     // still going

        var now = T0.AddMinutes(30);
        var s = _store.Outages("nas", T0, now, now);
        Assert.Equal(2, s.Count);

        var finished = s.Outages.Single(o => o.End is not null);
        Assert.Equal(15 * 60, finished.DurationSeconds);
        Assert.Equal(100, finished.StartCharge);
        Assert.InRange(finished.LowestCharge!.Value, 69, 71);   // 100 - 900/30
        Assert.Equal("Power restored", finished.EndReason);
        Assert.False(finished.ShutdownTriggered);

        var ongoing = s.Outages.Single(o => o.End is null);
        Assert.Equal(120, ongoing.DurationSeconds);
        Assert.Equal("Still on battery", ongoing.EndReason);

        Assert.Equal(15 * 60, s.LongestSeconds);
        Assert.Equal(17 * 60, s.TotalSeconds);
    }

    [Fact]
    public void Outage_that_ended_in_shutdown_is_flagged()
    {
        Event("OnBattery", T0);
        Event("ShutdownInitiated", T0.AddMinutes(20), "this-machine");
        Event("OnLine", T0.AddHours(2));
        Assert.True(_store.Outages("nas", T0, T0.AddHours(3), T0.AddHours(3)).Outages.Single().ShutdownTriggered);
    }

    [Fact]
    public void Repeated_power_failure_after_restart_is_one_outage()
    {
        Event("OnBattery", T0);
        Event("Connected", T0.AddMinutes(1));
        Event("OnBattery", T0.AddMinutes(1)); // reported again when the service restarted mid-outage
        Event("OnLine", T0.AddMinutes(5));
        var o = _store.Outages("nas", T0, T0.AddHours(1), T0.AddHours(1)).Outages.Single();
        Assert.Equal(300, o.DurationSeconds);
    }

    [Fact]
    public void Outages_overlapping_the_range_start_are_included()
    {
        Event("OnBattery", T0);
        Event("OnLine", T0.AddHours(2));
        Assert.Single(_store.Outages("nas", T0.AddHours(1), T0.AddHours(3), T0.AddHours(3)).Outages);
        Assert.Empty(_store.Outages("nas", T0.AddHours(3), T0.AddHours(4), T0.AddHours(4)).Outages);
    }

    [Fact]
    public void Csv_export_has_a_column_per_metric()
    {
        Sample(T0, 100, 20);
        Sample(T0.AddSeconds(15), 99.5, 21.25);
        var csv = new StringWriter();
        _store.ExportCsv(csv, "nas", null, T0, T0.AddMinutes(1), T0.AddMinutes(1));
        var lines = csv.ToString().Trim().Split(Environment.NewLine);
        Assert.Equal("time_utc,battery.charge (%),ups.load (%)", lines[0]);
        Assert.Equal("2026-11-01 00:00:00,100,20", lines[1]);
        Assert.Equal("2026-11-01 00:00:15,99.5,21.25", lines[2]);
    }

    [Fact]
    public void Data_survives_reopening_the_file()
    {
        Fill(T0, TimeSpan.FromMinutes(1), _ => 77);
        _store.Rollup(T0.AddMinutes(10));
        using (var again = new HistoryStore(_path))
        {
            var r = again.Query("nas", ["battery.charge"], T0, T0.AddMinutes(1), 100, T0.AddMinutes(1));
            Assert.Equal(4, r.Series[0].Points.Count);
        }
    }
}

public class MetricTests
{
    [Fact]
    public void Extracts_tracked_numbers_and_on_battery_flag()
    {
        var vars = new Dictionary<string, string>
        {
            ["battery.charge"] = "87", ["ups.load"] = "23.4", ["ups.status"] = "OB DISCHRG",
            ["ups.model"] = "FX-3000RT", ["battery.charge.low"] = "20", ["input.voltage.nominal"] = "120",
            ["input.L2-N.voltage"] = "229.8", ["ambient.2.humidity"] = "42.0", ["outlet.1.current"] = "2.9",
        };
        var m = MetricExtractor.Extract(vars).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(87, m["battery.charge"]);
        Assert.Equal(1, m[MetricCatalog.OnBattery]);
        Assert.Equal(229.8, m["input.L2-N.voltage"]);
        Assert.Equal(42, m["ambient.2.humidity"]);
        Assert.Equal(2.9, m["outlet.1.current"]);
        Assert.DoesNotContain("battery.charge.low", m.Keys);     // a setting, not a reading
        Assert.DoesNotContain("input.voltage.nominal", m.Keys);
        Assert.DoesNotContain("ups.model", m.Keys);
    }

    [Fact]
    public void No_chart_ever_needs_more_than_two_units()
    {
        // Every metric any simulator profile reports, plus the fixed ones.
        var names = new[] { "full-featured", "back-ups", "three-phase" }
            .SelectMany(p => WallNut.Simulator.UpsProfile.Load(p).Vars.Keys)
            .Append(MetricCatalog.OnBattery)
            .Where(MetricCatalog.IsTracked)
            .Distinct();
        var byChart = names.Select(n => MetricCatalog.Describe(n)!)
            .GroupBy(m => m.Chart)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Metric == "battery.runtime" ? "min" : m.Unit).Distinct().ToList());

        Assert.All(byChart, kv => Assert.True(kv.Value.Count <= 2, $"{kv.Key} has units {string.Join(", ", kv.Value)}"));
        Assert.True(byChart.Count >= 10, "expected the three profiles to fill most charts");
    }

    [Theory]
    [InlineData("input.L1-N.voltage", "Input L1-N", "V", MetricChart.PhaseVoltage)]
    [InlineData("output.L3.current", "Output L3 current", "A", MetricChart.PhaseCurrent)]
    [InlineData("output.L2.power.percent", "Output L2 load", "%", MetricChart.Load)]
    [InlineData("output.L2.realpower", "Output L2 real power", "W", MetricChart.PhasePower)]
    [InlineData("battery.voltage", "Battery voltage", "V", MetricChart.BatteryElectrical)]
    [InlineData("ambient.1.temperature", "Probe 1", "°C", MetricChart.Temperature)]
    [InlineData("battery.runtime", "Runtime left", "s", MetricChart.Battery)]
    public void Labels_and_units(string metric, string label, string unit, MetricChart chart)
    {
        var m = MetricCatalog.Describe(metric)!;
        Assert.Equal(label, m.Label);
        Assert.Equal(unit, m.Unit);
        Assert.Equal(chart, m.Chart);
    }
}
