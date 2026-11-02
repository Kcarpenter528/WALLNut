using System.Globalization;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;

namespace WallNut.Service.Shutdown;

/// <summary>How one UPS that powers this machine looks right now.</summary>
public sealed record SupplyAssessment(string EndpointId, string DisplayName, bool Critical, int PowerValue, string Reason);

/// <summary>The shutdown decision and why.</summary>
public sealed record ShutdownDecision(
    bool ShouldShutDown,
    int HealthySupplies,
    int MinimumSupplies,
    IReadOnlyList<SupplyAssessment> Supplies)
{
    public static readonly ShutdownDecision Nothing = new(false, 0, 0, []);

    public string Summary => ShouldShutDown
        ? $"{HealthySupplies} of the {MinimumSupplies} needed power supplies are healthy. " +
          string.Join("; ", Supplies.Where(s => s.Critical).Select(s => $"{s.DisplayName}: {s.Reason}")) + "."
        : $"{HealthySupplies} healthy power supplies (need {MinimumSupplies}).";
}

/// <summary>
/// Decides whether this machine must shut down, the way NUT's upsmon does: each UPS that powers
/// this machine is healthy or critical, healthy ones add their PowerValue, and if the total falls
/// below MinimumSupplies it's time to go. A pure function of the current state, so it's easy to test.
/// </summary>
public static class ShutdownPolicy
{
    public static ShutdownDecision Evaluate(IEnumerable<UpsState> states, ShutdownOptions options, DateTimeOffset now)
    {
        var supplies = states
            .Where(s => s.Role == UpsRole.PowersThisMachine)
            .Select(s => Assess(s, options, now))
            .ToList();
        if (!options.Enabled || supplies.Count == 0) return ShutdownDecision.Nothing with { Supplies = supplies };

        var healthy = supplies.Where(s => !s.Critical).Sum(s => s.PowerValue);
        return new ShutdownDecision(healthy < options.MinimumSupplies, healthy, options.MinimumSupplies, supplies);
    }

    public static SupplyAssessment Assess(UpsState s, ShutdownOptions o, DateTimeOffset now)
    {
        SupplyAssessment Critical(string why) => new(s.EndpointId, s.DisplayName, true, s.PowerValue, why);
        SupplyAssessment Healthy(string why) => new(s.EndpointId, s.DisplayName, false, s.PowerValue, why);

        // 1. What the last reading says (still applies if contact has since been lost).
        if (s.Status is { } st)
        {
            if (o.RespectForcedShutdown && st.IsForcedShutdown) return Critical("the NUT server is forcing a shutdown (FSD)");
            if (st.IsOnBattery)
            {
                if (o.OnLowBattery && st.IsLowBattery) return Critical("on battery and the UPS reports low battery");

                if (o.BatteryChargeBelowPercent is { } minCharge && Number(s.Var("battery.charge")) is { } c && c < minCharge)
                    return Critical($"on battery at {c:0}%, below the {minCharge:0}% limit");

                if (o.RuntimeBelowSeconds is { } minRuntime && Number(s.Var("battery.runtime")) is { } r && r < minRuntime)
                    return Critical($"on battery with {r:0}s left, below the {minRuntime:0}s limit");

                if (o.OnBatteryForSeconds is { } maxOnBattery && s.OnBatterySince is { } since
                    && (now - since).TotalSeconds is var onFor && onFor >= maxOnBattery)
                    return Critical($"on battery for {onFor:0}s, the {maxOnBattery:0}s limit");
            }
        }

        // 2. Fail-safe: without fresh data we can't see a battery running out.
        var blindSince = !s.Connected ? s.DisconnectedSince : s.Stale ? s.StaleSince : null;
        bool lastSeenOnBattery = s.Status?.IsOnBattery == true;
        if (blindSince is { } blind)
        {
            var blindFor = (now - blind).TotalSeconds;
            var what = s.Connected ? "fresh data" : "contact";
            if (!lastSeenOnBattery)
                return Healthy(s.Status is null
                    ? "not reached yet; assumed to be supplying power"
                    : $"no {what}, but it was last on line power");
            return blindFor >= o.LostWhileOnBatterySeconds
                ? Critical($"no {what} for {blindFor:0}s and it was last seen on battery")
                : Healthy($"no {what} for {blindFor:0}s while on battery (fail-safe at {o.LostWhileOnBatterySeconds:0}s)");
        }

        if (s.Status is not { } status) return Healthy("waiting for the first reading");
        if (!status.IsOnBattery) return Healthy("on line power");
        var charge = Number(s.Var("battery.charge"));
        return Healthy($"on battery{(charge is { } ch ? $" at {ch:0}%" : "")}, not critical yet");
    }

    private static double? Number(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
