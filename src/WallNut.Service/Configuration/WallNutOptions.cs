namespace WallNut.Service.Configuration;

/// <summary>What a UPS means to this machine.</summary>
public enum UpsRole
{
    /// <summary>Watch it and send alerts, but never shut this machine down because of it.</summary>
    Monitor,

    /// <summary>This machine is plugged into it: its power state can trigger a shutdown here.</summary>
    PowersThisMachine,
}

/// <summary>One UPS on one NUT server. Each endpoint gets its own connection.</summary>
public sealed class EndpointOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = Nut.NutProtocol.DefaultPort;

    /// <summary>The UPS name on that server, e.g. "ups" on a Synology NAS.</summary>
    public string Ups { get; set; } = "ups";

    /// <summary>Optional. Needed when <see cref="Role"/> is PowersThisMachine (to LOGIN like upsmon) or when the server requires it for reads.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    public UpsRole Role { get; set; } = UpsRole.Monitor;

    /// <summary>
    /// How many of this machine's power supplies this UPS feeds (NUT's POWERVALUE). Usually 1.
    /// Compared against Shutdown:MinimumSupplies; only used when Role is PowersThisMachine.
    /// </summary>
    public int PowerValue { get; set; } = 1;

    /// <summary>A friendly name for alerts and the dashboard. Defaults to the endpoint's key.</summary>
    public string? DisplayName { get; set; }

    public double PollIntervalSeconds { get; set; } = 5;

    /// <summary>How long to wait for a connection or a reply. Over 2 s on Windows, which retries refused connections for ~2 s.</summary>
    public double TimeoutSeconds { get; set; } = 10;

    public bool Enabled { get; set; } = true;
}

public enum ShutdownAction { Shutdown, Hibernate }

/// <summary>When and how to shut this machine down. Only UPSes with Role PowersThisMachine count.</summary>
public sealed class ShutdownOptions
{
    /// <summary>Master switch. Off = never shut down (alerts still work).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Test mode: log "would shut down now" instead of doing it. On until you turn it off.</summary>
    public bool DryRun { get; set; } = true;

    public ShutdownAction Action { get; set; } = ShutdownAction.Shutdown;

    /// <summary>Shut down when fewer than this many supplies are healthy (NUT's MINSUPPLIES). Usually 1.</summary>
    public int MinimumSupplies { get; set; } = 1;

    /// <summary>Obey the NUT server's forced-shutdown flag (what a NAS sets when it is about to power off).</summary>
    public bool RespectForcedShutdown { get; set; } = true;

    /// <summary>Treat the UPS's own low-battery flag (on battery + LB) as critical. The NUT default.</summary>
    public bool OnLowBattery { get; set; } = true;

    /// <summary>Optional: also critical when on battery and the charge falls below this percent.</summary>
    public double? BatteryChargeBelowPercent { get; set; }

    /// <summary>Optional: also critical when on battery and the remaining runtime falls below this many seconds.</summary>
    public double? RuntimeBelowSeconds { get; set; }

    /// <summary>Optional: also critical once on battery for this long, whatever the charge.</summary>
    public double? OnBatteryForSeconds { get; set; }

    /// <summary>
    /// Fail-safe: when contact with a UPS that was last seen ON BATTERY has been lost (or its data has
    /// been stale) for this long, assume the worst. NUT's DEADTIME. Lost contact while on line power
    /// is not critical: the UPS is assumed to still be powering us.
    /// </summary>
    public double LostWhileOnBatterySeconds { get; set; } = 15;

    /// <summary>Wait this long after deciding, so alerts can go out first (NUT's FINALDELAY).</summary>
    public double FinalDelaySeconds { get; set; } = 5;
}

public sealed class ReconnectOptions
{
    public double InitialDelaySeconds { get; set; } = 2;
    public double MaxDelaySeconds { get; set; } = 60;
}

/// <summary>The "WallNut" section of appsettings.json.</summary>
public sealed class WallNutOptions
{
    public const string Section = "WallNut";

    /// <summary>Keyed by a short id (e.g. "nas"), so config files can add or override endpoints by name.</summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ReconnectOptions Reconnect { get; set; } = new();

    public ShutdownOptions Shutdown { get; set; } = new();

    /// <summary>Problems that would stop monitoring from working, in plain words. Empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var enabled = Endpoints.Where(e => e.Value.Enabled).ToList();
        if (enabled.Count == 0) problems.Add("No UPS endpoints are configured. Add one under WallNut:Endpoints.");

        foreach (var (id, e) in enabled)
        {
            if (string.IsNullOrWhiteSpace(e.Host)) problems.Add($"Endpoint '{id}': Host is required.");
            if (string.IsNullOrWhiteSpace(e.Ups)) problems.Add($"Endpoint '{id}': Ups (the UPS name on the server) is required.");
            if (e.Port is < 1 or > 65535) problems.Add($"Endpoint '{id}': Port {e.Port} is not valid.");
            if (e.PollIntervalSeconds <= 0) problems.Add($"Endpoint '{id}': PollIntervalSeconds must be more than 0.");
            if (e.TimeoutSeconds <= 0) problems.Add($"Endpoint '{id}': TimeoutSeconds must be more than 0.");
            if ((e.Username is null) != (e.Password is null))
                problems.Add($"Endpoint '{id}': set both Username and Password, or neither.");
            if (e.Role == UpsRole.PowersThisMachine && e.Username is null)
                problems.Add($"Endpoint '{id}': a UPS that powers this machine needs a Username and Password (for Synology: monuser / secret).");
        }
        if (Reconnect.InitialDelaySeconds <= 0 || Reconnect.MaxDelaySeconds < Reconnect.InitialDelaySeconds)
            problems.Add("Reconnect delays must be positive, with MaxDelaySeconds at least InitialDelaySeconds.");

        foreach (var (id, e) in enabled.Where(e => e.Value.Role == UpsRole.PowersThisMachine))
        {
            if (e.PowerValue < 0) problems.Add($"Endpoint '{id}': PowerValue can't be negative.");
        }
        var supplies = enabled.Where(e => e.Value.Role == UpsRole.PowersThisMachine).Sum(e => e.Value.PowerValue);
        if (supplies > 0 && Shutdown.MinimumSupplies > supplies)
            problems.Add($"Shutdown:MinimumSupplies is {Shutdown.MinimumSupplies} but the UPSes that power this machine " +
                         $"only add up to {supplies}, so it would shut down immediately.");
        if (Shutdown.MinimumSupplies < 1) problems.Add("Shutdown:MinimumSupplies must be at least 1.");
        if (Shutdown.LostWhileOnBatterySeconds < 0 || Shutdown.FinalDelaySeconds < 0)
            problems.Add("Shutdown delays can't be negative.");
        return problems;
    }
}
