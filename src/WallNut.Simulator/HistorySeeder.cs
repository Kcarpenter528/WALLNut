using WallNut.History;
using WallNut.Nut;
using WallNut.Shared;

namespace WallNut.Simulator;

/// <summary>
/// Fills a history database with realistic past data by running simulated UPSes through virtual
/// time: a daily load pattern, random power cuts (mostly short, a few long enough to drain the
/// battery), and the events the service would have recorded. For trying the charts without
/// waiting weeks.
/// </summary>
public static class HistorySeeder
{
    public sealed record Device(string EndpointId, string DisplayName, string Profile);

    public static readonly Device[] DevDefaults =
    [
        new("sim-rack", "Sim rack (full-featured)", "full-featured"),
        new("sim-office", "Sim office (Back-UPS)", "back-ups"),
        new("sim-dc", "Sim data center (3-phase)", "three-phase"),
    ];

    public static string DefaultDatabase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALLNut", "history-dev.db");

    public static void Seed(string databasePath, IEnumerable<Device> devices, int days, int seed, TextWriter log)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddDays(-days);
        using var store = new HistoryStore(databasePath);
        long seq = 0;

        foreach (var (device, index) in devices.Select((d, i) => (d, i)))
        {
            var rng = new Random(seed + index * 7919);
            var ups = new SimulatedUps(device.EndpointId, UpsProfile.Load(device.Profile), start.LocalDateTime, seed + index);
            var baseLoad = ups.LoadPercent;
            var outages = PlanOutages(rng, start, now);
            var batch = new List<(string, string, DateTimeOffset, IEnumerable<KeyValuePair<string, double>>)>();
            var previous = UpsStatus.Parse(ups.Get("ups.status"));
            var outageIndex = 0;
            var samples = 0;

            void Event(string kind, DateTimeOffset t, string message, bool problem) =>
                store.AddEvent(new ServiceEvent(++seq, device.EndpointId, device.DisplayName, kind, t, ups.Get("ups.status"), message, problem));

            Event("Connected", start, $"Connected to {device.EndpointId} (simulated history).", false);

            for (var t = start; t < now;)
            {
                // Recent days at full detail, older days every minute (they get condensed anyway).
                var step = now - t > TimeSpan.FromDays(3) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15);

                // Daily rhythm: busier in the working day, quiet at night, a little noise.
                var hour = t.ToLocalTime().Hour + t.ToLocalTime().Minute / 60.0;
                var daytime = Math.Max(0, Math.Sin((hour - 7) / 12 * Math.PI));
                var weekend = t.ToLocalTime().DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.75 : 1;
                ups.SetLoad(baseLoad * weekend * (0.65 + 0.6 * daytime) + (rng.NextDouble() - 0.5) * 2);

                // Power cuts.
                if (outageIndex < outages.Count && t >= outages[outageIndex].Start && !ups.IsOnBattery) ups.PowerFail();
                if (outageIndex < outages.Count && t >= outages[outageIndex].End) { ups.PowerRestore(); outageIndex++; }
                if (outageIndex < outages.Count && t >= outages[outageIndex].Start && t < outages[outageIndex].End && !ups.IsOnBattery) ups.PowerFail();

                ups.Advance(step);
                t += step;

                var status = UpsStatus.Parse(ups.Get("ups.status"));
                if (status.IsOnBattery && !previous.IsOnBattery)
                    Event("OnBattery", t, $"Power failure: running on battery (battery {ups.Get("battery.charge")}%).", true);
                if (!status.IsOnBattery && previous.IsOnBattery)
                    Event("OnLine", t, $"Power is back (battery {ups.Get("battery.charge")}%).", false);
                if (status.IsLowBattery && status.IsOnBattery && !previous.IsLowBattery)
                    Event("LowBattery", t, $"Battery is low (battery {ups.Get("battery.charge")}%).", true);
                if (status.Has(UpsStatusFlags.Offline) && !previous.Has(UpsStatusFlags.Offline))
                    Event("Off", t, "The UPS has turned its outlets off.", true);
                previous = status;

                batch.Add((device.EndpointId, device.DisplayName, t,
                    MetricExtractor.Extract(ups.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value)).ToList()));
                samples++;
                if (batch.Count >= 2000) { store.AddSampleBatch(batch); batch.Clear(); }
            }
            store.AddSampleBatch(batch);
            log.WriteLine($"  {device.EndpointId,-11} {device.Profile,-14} {samples,7} samples, {outages.Count} outages");
        }

        store.Rollup(now);
        store.ApplyRetention(now);
        store.Compact();
        log.WriteLine($"Wrote {days} days of history to {databasePath}");
    }

    /// <summary>Roughly two outages a week: mostly a few minutes, sometimes long enough to run the battery down.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> PlanOutages(Random rng, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<(DateTimeOffset, DateTimeOffset)>();
        var t = from.AddHours(rng.NextDouble() * 48);
        while (t < to.AddHours(-2))
        {
            var minutes = rng.NextDouble() switch
            {
                < 0.55 => 0.5 + rng.NextDouble() * 4,   // blips and short cuts
                < 0.9 => 5 + rng.NextDouble() * 20,     // a while
                _ => 30 + rng.NextDouble() * 60,        // long enough to drain a small UPS
            };
            list.Add((t, t.AddMinutes(minutes)));
            t = t.AddHours(12 + rng.NextDouble() * 120);
        }
        return list;
    }
}
