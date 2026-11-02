using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WallNut.Shared;

/// <summary>Talks to the local WALLNut service over its named pipe / Unix socket.</summary>
public sealed class WallNutServiceClient : IDisposable
{
    public static JsonSerializerOptions Json => WallNutJson.Options;

    private readonly HttpClient _http;

    public string Endpoint { get; }

    public WallNutServiceClient(string? endpoint = null)
    {
        Endpoint = endpoint is null ? ServiceEndpoint.Default : ServiceEndpoint.Resolve(endpoint);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                if (ServiceEndpoint.IsSocketPath(Endpoint))
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
                var pipe = new NamedPipeClientStream(".", Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(3), ct);
                    return pipe;
                }
                catch { await pipe.DisposeAsync(); throw; }
            },
        };
        // The host name is a placeholder: every connection goes to the pipe/socket above.
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://wallnut.local/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ServiceInfo> GetInfoAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await _http.GetFromJsonAsync("api/info", WallNutJsonContext.Default.ServiceInfo, cts.Token)
               ?? throw new InvalidDataException("Empty reply from the WALLNut service.");
    }

    public async Task<IReadOnlyList<ServiceEvent>> GetRecentEventsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("api/events/recent", WallNutJsonContext.Default.ListServiceEvent, ct) ?? [];

    // ---- History ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<History.HistoryEndpoint>> GetHistoryEndpointsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("api/history/endpoints", WallNutJsonContext.Default.ListHistoryEndpoint, ct) ?? [];

    public async Task<IReadOnlyList<History.MetricAvailability>> GetHistoryMetricsAsync(string endpoint, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync($"api/history/metrics?endpoint={Uri.EscapeDataString(endpoint)}",
            WallNutJsonContext.Default.ListMetricAvailability, ct) ?? [];

    /// <summary>Readings for a time range, in about <paramref name="points"/> buckets per metric. No metrics = all recorded ones.</summary>
    public async Task<History.HistoryResult> GetHistoryAsync(string endpoint, DateTimeOffset from, DateTimeOffset to,
        IEnumerable<string>? metrics = null, int points = 800, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync(
            $"api/history/series?endpoint={Uri.EscapeDataString(endpoint)}&from={Iso(from)}&to={Iso(to)}&points={points}" +
            (metrics is null ? "" : "&metrics=" + Uri.EscapeDataString(string.Join(',', metrics))),
            WallNutJsonContext.Default.HistoryResult, ct)
        ?? throw new InvalidDataException("Empty history reply.");

    public async Task<IReadOnlyList<ServiceEvent>> GetHistoryEventsAsync(string? endpoint, DateTimeOffset from, DateTimeOffset to,
        int limit = 500, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync(
            $"api/history/events?from={Iso(from)}&to={Iso(to)}&limit={limit}" + (endpoint is null ? "" : "&endpoint=" + Uri.EscapeDataString(endpoint)),
            WallNutJsonContext.Default.ListServiceEvent, ct) ?? [];

    public async Task<History.OutageSummary> GetOutagesAsync(string? endpoint, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync(
            $"api/history/outages?from={Iso(from)}&to={Iso(to)}" + (endpoint is null ? "" : "&endpoint=" + Uri.EscapeDataString(endpoint)),
            WallNutJsonContext.Default.OutageSummary, ct)
        ?? throw new InvalidDataException("Empty outage reply.");

    /// <summary>Downloads readings as CSV into <paramref name="destination"/>.</summary>
    public async Task ExportCsvAsync(string endpoint, DateTimeOffset from, DateTimeOffset to, Stream destination, CancellationToken ct = default)
    {
        using var reply = await _http.GetAsync($"api/history/export?endpoint={Uri.EscapeDataString(endpoint)}&from={Iso(from)}&to={Iso(to)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        reply.EnsureSuccessStatusCode();
        await reply.Content.CopyToAsync(destination, ct);
    }

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Asks the service to send a test event through every channel (desktop, and later phone and webhooks).</summary>
    public async Task SendTestAsync(CancellationToken ct = default)
    {
        using var reply = await _http.PostAsync("api/notifications/test", null, ct);
        reply.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Live events (Server-Sent Events). Ends when the service goes away; the caller reconnects.
    /// Only new events are streamed, so reconnecting never repeats old notifications.
    /// </summary>
    public async IAsyncEnumerable<ServiceEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/events/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        reply.EnsureSuccessStatusCode();
        await using var stream = await reply.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line.AsSpan(5).TrimStart());
            else if (line.Length == 0 && data.Length > 0)
            {
                var e = JsonSerializer.Deserialize(data.ToString(), WallNutJsonContext.Default.ServiceEvent);
                data.Clear();
                if (e is not null) yield return e;
            }
            // Lines starting with ':' are keep-alive pings.
        }
    }

    public void Dispose() => _http.Dispose();
}
