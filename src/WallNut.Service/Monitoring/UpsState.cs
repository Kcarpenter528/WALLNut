using System.Collections.Concurrent;
using WallNut.Nut;
using WallNut.Service.Configuration;

namespace WallNut.Service.Monitoring;

/// <summary>The latest known state of one UPS endpoint.</summary>
public sealed record UpsState
{
    public required string EndpointId { get; init; }
    public required string DisplayName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Ups { get; init; }
    public required UpsRole Role { get; init; }
    public int PowerValue { get; init; } = 1;

    public bool Connected { get; init; }
    public bool Stale { get; init; }
    public DateTimeOffset? ConnectedSince { get; init; }
    public DateTimeOffset? LastUpdate { get; init; }
    public string? LastError { get; init; }

    /// <summary>Last status read. Kept while disconnected, so the shutdown logic knows what it was.</summary>
    public UpsStatus? Status { get; init; }

    /// <summary>When the UPS went on battery (null while on line power). Kept while disconnected.</summary>
    public DateTimeOffset? OnBatterySince { get; init; }

    /// <summary>When contact was lost (null while connected or before the first attempt fails).</summary>
    public DateTimeOffset? DisconnectedSince { get; init; }

    /// <summary>When the driver started reporting stale data (null when data is fresh).</summary>
    public DateTimeOffset? StaleSince { get; init; }

    /// <summary>All variables from the last successful read.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } = new Dictionary<string, string>();

    public string? Var(string name) => Vars.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Thread-safe latest state for every endpoint (read by the dashboard, shutdown logic, etc.).</summary>
public sealed class UpsStateStore
{
    private readonly ConcurrentDictionary<string, UpsState> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<UpsState> All => _states.Values.OrderBy(s => s.EndpointId).ToList();

    public UpsState? Get(string endpointId) => _states.TryGetValue(endpointId, out var s) ? s : null;

    public void Set(UpsState state) => _states[state.EndpointId] = state;

    /// <summary>Forgets a UPS that's no longer configured.</summary>
    public void Remove(string endpointId) => _states.TryRemove(endpointId, out _);
}
