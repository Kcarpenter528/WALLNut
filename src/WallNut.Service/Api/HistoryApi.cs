using System.Globalization;
using WallNut.Service.HistoryRecording;

namespace WallNut.Service.Api;

/// <summary>History endpoints on the local API (named pipe / Unix socket only).</summary>
public static class HistoryApi
{
    public static void MapHistoryApi(this RouteGroupBuilder api)
    {
        var h = api.MapGroup("/history");

        h.MapGet("/endpoints", (HistoryDatabase db) => Use(db, s => Results.Ok(s.Endpoints())));

        h.MapGet("/metrics", (HistoryDatabase db, string endpoint) => Use(db, s => Results.Ok(s.Metrics(endpoint))));

        h.MapGet("/series", (HistoryDatabase db, TimeProvider time, string endpoint, string? metrics, string? from, string? to, int? points) =>
            Use(db, s =>
            {
                var (f, t, now) = Range(time, from, to);
                var names = metrics is { Length: > 0 } ? Split(metrics) : s.Metrics(endpoint).Select(m => m.Metric);
                return Results.Ok(s.Query(endpoint, names, f, t, points ?? 800, now));
            }));

        h.MapGet("/events", (HistoryDatabase db, TimeProvider time, string? endpoint, string? from, string? to, int? limit) =>
            Use(db, s =>
            {
                var (f, t, _) = Range(time, from, to);
                return Results.Ok(s.Events(endpoint, f, t, limit ?? 500));
            }));

        h.MapGet("/outages", (HistoryDatabase db, TimeProvider time, string? endpoint, string? from, string? to) =>
            Use(db, s =>
            {
                var (f, t, now) = Range(time, from, to);
                return Results.Ok(s.Outages(endpoint, f, t, now));
            }));

        h.MapGet("/export", (HistoryDatabase db, TimeProvider time, string endpoint, string? metrics, string? from, string? to) =>
            Use(db, s =>
            {
                var (f, t, now) = Range(time, from, to);
                var csv = new StringWriter(CultureInfo.InvariantCulture);
                s.ExportCsv(csv, endpoint, metrics is { Length: > 0 } ? Split(metrics) : null, f, t, now);
                var name = $"wallnut-{endpoint}-{f.UtcDateTime:yyyyMMdd-HHmm}-{t.UtcDateTime:yyyyMMdd-HHmm}.csv";
                return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", name);
            }));
    }

    private static IResult Use(HistoryDatabase db, Func<History.HistoryStore, IResult> action) =>
        db.Store is { } store ? action(store) : Results.Problem(db.Problem, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Times are ISO 8601. Defaults: the last 24 hours.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, DateTimeOffset Now) Range(TimeProvider time, string? from, string? to)
    {
        var now = time.GetUtcNow();
        var t = Parse(to) ?? now;
        var f = Parse(from) ?? t.AddDays(-1);
        if (f >= t) f = t.AddMinutes(-1);
        return (f, t, now);

        static DateTimeOffset? Parse(string? s) =>
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
