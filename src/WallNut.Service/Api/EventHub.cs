using System.Threading.Channels;
using WallNut.Service.Monitoring;
using WallNut.Shared;

namespace WallNut.Service.Api;

/// <summary>
/// Fans events out to connected apps. Numbers every event, keeps the most recent ones for
/// GET /api/events/recent, and gives each live subscriber its own queue so a slow app can
/// never hold up monitoring (it just loses the oldest events if it falls far behind).
/// </summary>
public sealed class EventHub : IUpsEventSink
{
    private const int RecentLimit = 200;
    private readonly object _gate = new();
    private readonly LinkedList<ServiceEvent> _recent = new();
    private readonly List<Channel<ServiceEvent>> _subscribers = [];
    private long _sequence;

    public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

    public Task PublishAsync(UpsEvent e, CancellationToken ct)
    {
        lock (_gate)
        {
            var dto = new ServiceEvent(++_sequence, e.EndpointId, e.DisplayName, e.Kind.ToString(), e.Time,
                e.Status?.Raw, e.Message, e.IsProblem);
            _recent.AddLast(dto);
            if (_recent.Count > RecentLimit) _recent.RemoveFirst();
            foreach (var s in _subscribers) s.Writer.TryWrite(dto);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<ServiceEvent> Recent()
    {
        lock (_gate) return _recent.ToList();
    }

    /// <summary>A live feed of new events. Dispose the subscription to stop.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ServiceEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public sealed class Subscription(EventHub hub, Channel<ServiceEvent> channel) : IDisposable
    {
        public ChannelReader<ServiceEvent> Reader => channel.Reader;

        public void Dispose()
        {
            lock (hub._gate) hub._subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
