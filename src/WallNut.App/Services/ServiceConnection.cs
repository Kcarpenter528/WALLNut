using WallNut.Shared;

namespace WallNut.App.Services;

/// <summary>
/// Keeps the tray app connected to the local WALLNut service: streams live events and refreshes
/// the status every few seconds, reconnecting forever if the service stops or restarts.
/// </summary>
public sealed class ServiceConnection : IDisposable
{
    private readonly WallNutServiceClient _client;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Latest status, or null while the service can't be reached. Raised on a background thread.</summary>
    public event Action<ServiceInfo?>? InfoChanged;

    /// <summary>A live event from the service. Raised on a background thread.</summary>
    public event Action<ServiceEvent>? EventReceived;

    public ServiceInfo? Info { get; private set; }
    public bool IsConnected => Info is not null;
    public WallNutServiceClient Client => _client;

    public ServiceConnection(string? endpoint = null) => _client = new WallNutServiceClient(endpoint);

    public void Start()
    {
        _ = Task.Run(() => StreamLoop(_stop.Token));
        _ = Task.Run(() => InfoLoop(_stop.Token));
    }

    private async Task StreamLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in _client.StreamEventsAsync(ct))
                {
                    EventReceived?.Invoke(e);
                    _ = RefreshAsync(ct); // status changed; don't wait for the next poll
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* service not running or restarting */ }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task InfoLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        ServiceInfo? info;
        try { info = await _client.GetInfoAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { info = null; }
        Info = info;
        InfoChanged?.Invoke(info);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
    }
}
