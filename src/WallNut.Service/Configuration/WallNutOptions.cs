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

    /// <summary>A friendly name for alerts and the dashboard. Defaults to the endpoint's key.</summary>
    public string? DisplayName { get; set; }

    public double PollIntervalSeconds { get; set; } = 5;

    /// <summary>How long to wait for a connection or a reply. Over 2 s on Windows, which retries refused connections for ~2 s.</summary>
    public double TimeoutSeconds { get; set; } = 10;

    public bool Enabled { get; set; } = true;
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
        return problems;
    }
}
