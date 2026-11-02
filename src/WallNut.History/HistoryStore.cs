using System.Globalization;
using Microsoft.Data.Sqlite;
using WallNut.Shared;
using WallNut.Shared.History;

namespace WallNut.History;

/// <summary>How long each level of detail is kept.</summary>
public sealed record HistoryRetention(int RawDays = 3, int FiveMinuteDays = 90, int HourlyDays = 730, int EventDays = 730);

/// <summary>
/// UPS readings and events in a single SQLite file. Readings are kept at three levels of detail:
/// every sample for a few days, 5-minute buckets for months, hourly buckets for years. Each bucket
/// keeps the average, minimum and maximum, so short dips and spikes stay visible after condensing.
/// Thread-safe.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const int FiveMinutes = 300, OneHour = 3600;
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly Dictionary<(string Endpoint, string Metric), long> _seriesIds = [];

    public string Path { get; }
    public HistoryRetention Retention { get; }
    /// <summary>Seconds between raw samples (used to size queries).</summary>
    public int SampleSeconds { get; }

    public HistoryStore(string path, HistoryRetention? retention = null, int sampleSeconds = 15)
    {
        Path = path;
        Retention = retention ?? new HistoryRetention();
        SampleSeconds = Math.Max(1, sampleSeconds);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // release the file when disposed
        }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS endpoints (endpoint TEXT PRIMARY KEY, display TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS series (
                id INTEGER PRIMARY KEY, endpoint TEXT NOT NULL, metric TEXT NOT NULL,
                first_ts INTEGER, last_ts INTEGER, UNIQUE (endpoint, metric));
            CREATE TABLE IF NOT EXISTS raw (series INTEGER NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL,
                PRIMARY KEY (series, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS r5m (series INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL, min REAL, max REAL, n INTEGER,
                PRIMARY KEY (series, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS r1h (series INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL, min REAL, max REAL, n INTEGER,
                PRIMARY KEY (series, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, endpoint TEXT NOT NULL, display TEXT NOT NULL, kind TEXT NOT NULL,
                ts INTEGER NOT NULL, status TEXT, message TEXT NOT NULL, problem INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS events_ts ON events (ts);
            INSERT OR IGNORE INTO meta VALUES ('schema', '1'), ('wm5m', '0'), ('wm1h', '0');
            """);
    }

    // ---- Writing -------------------------------------------------------------------------

    /// <summary>Records one sample of several metrics for a UPS.</summary>
    public void AddSamples(string endpoint, string displayName, DateTimeOffset time, IEnumerable<KeyValuePair<string, double>> values)
    {
        var t = time.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var tx = Begin();
            Exec("INSERT INTO endpoints VALUES (@e, @d) ON CONFLICT (endpoint) DO UPDATE SET display = excluded.display",
                ("@e", endpoint), ("@d", displayName));
            using var insert = Command("INSERT OR REPLACE INTO raw VALUES (@s, @t, @v)");
            var s = insert.Parameters.Add("@s", SqliteType.Integer);
            insert.Parameters.AddWithValue("@t", t);
            var v = insert.Parameters.Add("@v", SqliteType.Real);
            using var touch = Command("UPDATE series SET first_ts = MIN(COALESCE(first_ts, @t), @t), last_ts = MAX(COALESCE(last_ts, @t), @t) WHERE id = @s");
            var ts = touch.Parameters.Add("@s", SqliteType.Integer);
            touch.Parameters.AddWithValue("@t", t);

            foreach (var (metric, value) in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                s.Value = ts.Value = SeriesId(endpoint, metric);
                v.Value = value;
                insert.ExecuteNonQuery();
                touch.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Many samples in one transaction (for importing or generating history).</summary>
    public void AddSampleBatch(IEnumerable<(string Endpoint, string DisplayName, DateTimeOffset Time, IEnumerable<KeyValuePair<string, double>> Values)> samples)
    {
        lock (_gate)
        {
            using var tx = Begin();
            using var insert = Command("INSERT OR REPLACE INTO raw VALUES (@s, @t, @v)");
            var s = insert.Parameters.Add("@s", SqliteType.Integer);
            var t = insert.Parameters.Add("@t", SqliteType.Integer);
            var v = insert.Parameters.Add("@v", SqliteType.Real);
            var range = new Dictionary<long, (long First, long Last)>();
            var names = new Dictionary<string, string>();

            foreach (var (endpoint, display, time, values) in samples)
            {
                names[endpoint] = display;
                var ts = time.ToUnixTimeSeconds();
                foreach (var (metric, value) in values)
                {
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    var id = SeriesId(endpoint, metric);
                    s.Value = id; t.Value = ts; v.Value = value;
                    insert.ExecuteNonQuery();
                    range[id] = range.TryGetValue(id, out var r) ? (Math.Min(r.First, ts), Math.Max(r.Last, ts)) : (ts, ts);
                }
            }
            foreach (var (endpoint, display) in names)
                Exec("INSERT INTO endpoints VALUES (@e, @d) ON CONFLICT (endpoint) DO UPDATE SET display = excluded.display", ("@e", endpoint), ("@d", display));
            foreach (var (id, (first, last)) in range)
                Exec("UPDATE series SET first_ts = MIN(COALESCE(first_ts, @f), @f), last_ts = MAX(COALESCE(last_ts, @l), @l) WHERE id = @s",
                    ("@s", id), ("@f", first), ("@l", last));
            tx.Commit();
        }
    }

    public void AddEvent(ServiceEvent e)
    {
        lock (_gate)
        {
            Exec("INSERT INTO events (endpoint, display, kind, ts, status, message, problem) VALUES (@e, @d, @k, @t, @s, @m, @p)",
                ("@e", e.EndpointId), ("@d", e.DisplayName), ("@k", e.Kind), ("@t", e.Time.ToUnixTimeMilliseconds()),
                ("@s", (object?)e.Status ?? DBNull.Value), ("@m", e.Message), ("@p", e.IsProblem ? 1 : 0));
        }
    }

    /// <summary>Condenses raw samples into 5-minute buckets and those into hourly ones, up to <paramref name="now"/>.</summary>
    public void Rollup(DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var tx = Begin();
            long wm5 = Meta("wm5m"), cut5 = t / FiveMinutes * FiveMinutes;
            if (cut5 > wm5)
            {
                Exec("""
                    INSERT OR REPLACE INTO r5m (series, ts, avg, min, max, n)
                    SELECT series, (ts / 300) * 300, AVG(value), MIN(value), MAX(value), COUNT(*)
                    FROM raw WHERE ts >= @lo AND ts < @hi GROUP BY series, (ts / 300) * 300
                    """, ("@lo", wm5), ("@hi", cut5));
                SetMeta("wm5m", cut5);
            }
            long wm1 = Meta("wm1h"), cut1 = t / OneHour * OneHour;
            if (cut1 > wm1)
            {
                Exec("""
                    INSERT OR REPLACE INTO r1h (series, ts, avg, min, max, n)
                    SELECT series, (ts / 3600) * 3600, SUM(avg * n) / SUM(n), MIN(min), MAX(max), SUM(n)
                    FROM r5m WHERE ts >= @lo AND ts < @hi GROUP BY series, (ts / 3600) * 3600
                    """, ("@lo", wm1), ("@hi", cut1));
                SetMeta("wm1h", cut1);
            }
            tx.Commit();
        }
    }

    /// <summary>Deletes data older than each level's retention.</summary>
    public void ApplyRetention(DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var tx = Begin();
            Exec("DELETE FROM raw WHERE ts < @c", ("@c", t - Retention.RawDays * 86400L));
            Exec("DELETE FROM r5m WHERE ts < @c", ("@c", t - Retention.FiveMinuteDays * 86400L));
            Exec("DELETE FROM r1h WHERE ts < @c", ("@c", t - Retention.HourlyDays * 86400L));
            Exec("DELETE FROM events WHERE ts < @c", ("@c", (t - Retention.EventDays * 86400L) * 1000));
            tx.Commit();
        }
    }

    /// <summary>Gives free space back to the disk (after deleting a lot at once, e.g. an import).</summary>
    public void Compact()
    {
        lock (_gate)
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE)");
            Exec("VACUUM");
        }
    }

    // ---- Reading -------------------------------------------------------------------------

    public IReadOnlyList<HistoryEndpoint> Endpoints()
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT e.endpoint, e.display, MIN(s.first_ts), MAX(s.last_ts)
                FROM endpoints e JOIN series s ON s.endpoint = e.endpoint
                GROUP BY e.endpoint ORDER BY e.display
                """);
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEndpoint>();
            while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), Time(r.GetInt64(3))));
            return list;
        }
    }

    public IReadOnlyList<MetricAvailability> Metrics(string endpoint)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT metric, first_ts, last_ts FROM series WHERE endpoint = @e AND first_ts IS NOT NULL ORDER BY metric", ("@e", endpoint));
            using var r = cmd.ExecuteReader();
            var list = new List<MetricAvailability>();
            while (r.Read())
            {
                var m = MetricCatalog.Describe(r.GetString(0)) ?? new MetricInfo(r.GetString(0), r.GetString(0), "", MetricChart.Status);
                list.Add(new(m.Metric, m.Label, m.Unit, m.Chart.ToString(), Time(r.GetInt64(1)), Time(r.GetInt64(2))));
            }
            return list;
        }
    }

    /// <summary>
    /// Readings for <paramref name="metrics"/> between <paramref name="from"/> and <paramref name="to"/>,
    /// in at most about <paramref name="maxPoints"/> buckets per metric. Uses the finest level of detail
    /// that covers the range, and fills the most recent minutes from finer data that hasn't been condensed yet.
    /// </summary>
    public HistoryResult Query(string endpoint, IEnumerable<string> metrics, DateTimeOffset from, DateTimeOffset to,
        int maxPoints, DateTimeOffset now)
    {
        long f = from.ToUnixTimeSeconds(), t = to.ToUnixTimeSeconds(), n = now.ToUnixTimeSeconds();
        if (t <= f) return new HistoryResult(endpoint, from, to, SampleSeconds, []);
        maxPoints = Math.Clamp(maxPoints, 10, 200_000);

        var bucket = (int)Math.Max(SampleSeconds, Math.Ceiling((t - f) / (double)maxPoints));
        int tier; // 0 raw, 1 five-minute, 2 hourly
        if (bucket >= OneHour || f < n - Retention.FiveMinuteDays * 86400L) tier = 2;
        else if (bucket >= FiveMinutes || f < n - Retention.RawDays * 86400L) tier = 1;
        else tier = 0;
        bucket = NiceBucket(bucket, tier);

        lock (_gate)
        {
            long wm5 = Meta("wm5m"), wm1 = Meta("wm1h");
            // Coarse data where it exists, finer data after the last condensing run.
            var parts = new List<string>();
            if (tier == 2) parts.Add("SELECT ts, avg, min, max, n FROM r1h WHERE series = @s AND ts >= @f AND ts < @t AND ts < @wm1");
            if (tier >= 1) parts.Add("SELECT ts, avg, min, max, n FROM r5m WHERE series = @s AND ts >= @lo5 AND ts < @t AND ts < @wm5");
            parts.Add("SELECT ts, value AS avg, value AS min, value AS max, 1 AS n FROM raw WHERE series = @s AND ts >= @loRaw AND ts < @t");
            var sql = $"""
                SELECT (ts / @b) * @b AS bt, SUM(avg * n) / SUM(n), MIN(min), MAX(max)
                FROM ({string.Join(" UNION ALL ", parts)}) GROUP BY bt ORDER BY bt
                """;

            var result = new List<HistorySeries>();
            foreach (var metric in metrics.Distinct())
            {
                var info = MetricCatalog.Describe(metric) ?? new MetricInfo(metric, metric, "", MetricChart.Status);
                var points = new List<HistoryPoint>();
                if (FindSeries(endpoint, metric) is { } id)
                {
                    using var cmd = Command(sql, ("@s", id), ("@f", f), ("@t", t), ("@b", bucket), ("@wm1", wm1), ("@wm5", wm5),
                        ("@lo5", tier == 2 ? Math.Max(f, wm1) : f), ("@loRaw", tier == 0 ? f : Math.Max(f, wm5)));
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) points.Add(new(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)));
                }
                result.Add(new(metric, info.Label, info.Unit, info.Chart.ToString(), points));
            }
            return new HistoryResult(endpoint, from, to, bucket, result);
        }
    }

    public IReadOnlyList<ServiceEvent> Events(string? endpoint, DateTimeOffset from, DateTimeOffset to, int limit = 500)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT seq, endpoint, display, kind, ts, status, message, problem FROM events
                WHERE ts >= @f AND ts < @t AND (@e IS NULL OR endpoint = @e OR endpoint = 'this-machine')
                ORDER BY ts DESC, seq DESC LIMIT @l
                """, ("@f", from.ToUnixTimeMilliseconds()), ("@t", to.ToUnixTimeMilliseconds()),
                ("@e", (object?)endpoint ?? DBNull.Value), ("@l", Math.Clamp(limit, 1, 10_000)));
            using var r = cmd.ExecuteReader();
            var list = new List<ServiceEvent>();
            while (r.Read())
                list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetString(6), r.GetInt64(7) != 0));
            return list;
        }
    }

    /// <summary>Power failures that overlap the range, worked out from the recorded events.</summary>
    public OutageSummary Outages(string? endpoint, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        // Look back far enough to find the start of an outage already in progress when the range begins.
        var all = Events(null, from.AddDays(-30), to, 10_000).OrderBy(e => e.Time).ThenBy(e => e.Sequence).ToList();
        var open = new Dictionary<string, (ServiceEvent Start, bool Shutdown)>();
        var outages = new List<Outage>();

        foreach (var e in all)
        {
            if (e.EndpointId == "this-machine")
            {
                if (e.Kind == "ShutdownInitiated")
                    foreach (var key in open.Keys.ToList()) open[key] = (open[key].Start, true);
                continue;
            }
            if (endpoint is not null && !e.EndpointId.Equals(endpoint, StringComparison.OrdinalIgnoreCase)) continue;

            if (e.Kind == "OnBattery")
            {
                open.TryAdd(e.EndpointId, (e, false)); // a repeat after a restart continues the same outage
            }
            else if (e.Kind == "OnLine" && open.Remove(e.EndpointId, out var o))
            {
                outages.Add(Build(o.Start, e.Time, o.Shutdown, "Power restored"));
            }
        }
        foreach (var o in open.Values) outages.Add(Build(o.Start, null, o.Shutdown, "Still on battery"));

        var inRange = outages.Where(o => o.Start < to && (o.End ?? now) >= from).OrderByDescending(o => o.Start).ToList();
        return new OutageSummary(inRange.Count, inRange.Sum(o => o.DurationSeconds),
            inRange.Count == 0 ? 0 : inRange.Max(o => o.DurationSeconds), inRange);

        Outage Build(ServiceEvent start, DateTimeOffset? end, bool shutdown, string reason)
        {
            var stop = end ?? now;
            var charge = Query(start.EndpointId, ["battery.charge"], start.Time.AddSeconds(-SampleSeconds * 2), stop.AddSeconds(SampleSeconds), 100_000, now)
                .Series[0].Points;
            double? first = charge.FirstOrDefault(p => p.T >= start.Time.ToUnixTimeSeconds() - SampleSeconds * 2)?.Avg;
            double? lowest = charge.Count == 0 ? null : charge.Min(p => p.Min);
            return new Outage(start.EndpointId, start.DisplayName, start.Time, end, (stop - start.Time).TotalSeconds,
                first, lowest, shutdown, reason);
        }
    }

    /// <summary>Writes readings as CSV: a UTC time column, then one column per metric.</summary>
    public void ExportCsv(TextWriter output, string endpoint, IEnumerable<string>? metrics, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var names = (metrics ?? Metrics(endpoint).Select(m => m.Metric)).ToList();
        var span = (to - from).TotalSeconds;
        var result = Query(endpoint, names, from, to, (int)Math.Min(200_000, Math.Max(10, span / SampleSeconds)), now);

        static string Esc(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        output.WriteLine("time_utc," + string.Join(',', result.Series.Select(s => Esc(string.IsNullOrEmpty(s.Unit) ? s.Metric : $"{s.Metric} ({s.Unit})"))));
        var byTime = new SortedDictionary<long, double?[]>();
        for (var i = 0; i < result.Series.Count; i++)
            foreach (var p in result.Series[i].Points)
            {
                if (!byTime.TryGetValue(p.T, out var row)) byTime[p.T] = row = new double?[result.Series.Count];
                row[i] = p.Avg;
            }
        foreach (var (t, row) in byTime)
            output.WriteLine(Time(t).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," +
                string.Join(',', row.Select(v => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "")));
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static readonly int[] NiceSizes =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800];

    /// <summary>Rounds a bucket size up to a familiar one (5 min, 15 min, 1 h, 6 h, 1 day...) the tier can produce.</summary>
    private int NiceBucket(int bucket, int tier)
    {
        var minimum = tier switch { 2 => OneHour, 1 => FiveMinutes, _ => SampleSeconds };
        foreach (var n in NiceSizes)
            if (n >= bucket && n >= minimum && n % Math.Min(minimum, n) == 0) return n;
        return (int)(Math.Ceiling(bucket / 86400.0) * 86400);
    }

    private long SeriesId(string endpoint, string metric)
    {
        if (_seriesIds.TryGetValue((endpoint, metric), out var id)) return id;
        Exec("INSERT OR IGNORE INTO series (endpoint, metric) VALUES (@e, @m)", ("@e", endpoint), ("@m", metric));
        using var cmd = Command("SELECT id FROM series WHERE endpoint = @e AND metric = @m", ("@e", endpoint), ("@m", metric));
        id = (long)cmd.ExecuteScalar()!;
        _seriesIds[(endpoint, metric)] = id;
        return id;
    }

    private long? FindSeries(string endpoint, string metric)
    {
        if (_seriesIds.TryGetValue((endpoint, metric), out var id)) return id;
        using var cmd = Command("SELECT id FROM series WHERE endpoint = @e AND metric = @m", ("@e", endpoint), ("@m", metric));
        return cmd.ExecuteScalar() is long found ? _seriesIds[(endpoint, metric)] = found : null;
    }

    private long Meta(string key)
    {
        using var cmd = Command("SELECT value FROM meta WHERE key = @k", ("@k", key));
        return long.Parse((string)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    private void SetMeta(string key, long value) =>
        Exec("UPDATE meta SET value = @v WHERE key = @k", ("@k", key), ("@v", value.ToString(CultureInfo.InvariantCulture)));

    private static DateTimeOffset Time(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    /// <summary>Starts a transaction that every command created until it's disposed joins automatically.</summary>
    private Tx Begin() => new(this, _db.BeginTransaction());

    private sealed class Tx(HistoryStore store, SqliteTransaction tx) : IDisposable
    {
        private readonly SqliteTransaction? _previous = Swap(store, tx);
        private static SqliteTransaction? Swap(HistoryStore s, SqliteTransaction t) { var p = s._tx; s._tx = t; return p; }
        public void Commit() => tx.Commit();
        public void Dispose() { store._tx = _previous; tx.Dispose(); }
    }

    private SqliteTransaction? _tx;

    private SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
