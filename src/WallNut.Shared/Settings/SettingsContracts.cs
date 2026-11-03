namespace WallNut.Shared.Settings;

/// <summary>One UPS connection as shown and edited in the app. Passwords are never sent back to apps.</summary>
public sealed record EndpointConfig
{
    public string Id { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 3493;
    public string Ups { get; init; } = "ups";
    public string? Username { get; init; }

    /// <summary>Sent by the app only when setting or changing it. Null keeps the stored password.</summary>
    public string? Password { get; init; }

    /// <summary>True when a password is stored (the service fills this in; apps never see the password).</summary>
    public bool PasswordSet { get; init; }

    /// <summary>"Monitor" or "PowersThisMachine".</summary>
    public string Role { get; init; } = "Monitor";
    public int PowerValue { get; init; } = 1;
    public double PollIntervalSeconds { get; init; } = 5;
    public double TimeoutSeconds { get; init; } = 10;
    public bool Enabled { get; init; } = true;
}

/// <summary>When and how this machine shuts down.</summary>
public sealed record ShutdownSettings
{
    public bool Enabled { get; init; } = true;
    public bool DryRun { get; init; } = true;
    /// <summary>"Shutdown" or "Hibernate".</summary>
    public string Action { get; init; } = "Shutdown";
    public int MinimumSupplies { get; init; } = 1;
    public bool RespectForcedShutdown { get; init; } = true;
    public bool OnLowBattery { get; init; } = true;
    public double? BatteryChargeBelowPercent { get; init; }
    public double? RuntimeBelowSeconds { get; init; }
    public double? OnBatteryForSeconds { get; init; }
    public double LostWhileOnBatterySeconds { get; init; } = 15;
    public double FinalDelaySeconds { get; init; } = 5;
}

/// <summary>The service's current settings, as an app may see them.</summary>
public sealed record SettingsView(
    string? ConfigFile,
    /// <summary>True when this caller may change settings (an administrator) and the file can be written.</summary>
    bool CanEdit,
    /// <summary>Why editing isn't possible, in plain words (null when it is).</summary>
    string? ReadOnlyReason,
    IReadOnlyList<EndpointConfig> Endpoints,
    ShutdownSettings Shutdown);

/// <summary>Result of trying a connection before saving it.</summary>
public sealed record EndpointTestResult(
    bool Ok,
    string Message,
    string? Model,
    string? Status,
    double? ChargePercent,
    /// <summary>UPS names the server offers, to help pick the right one.</summary>
    IReadOnlyList<string> AvailableUps,
    bool LoginWorks);

/// <summary>Who the service thinks is asking.</summary>
public sealed record CallerInfo(string? User, bool IsAdmin);

/// <summary>Problems that stopped a settings change, in plain words.</summary>
public sealed record SettingsProblem(IReadOnlyList<string> Problems);

/// <summary>Everything about one UPS right now, including every variable it reports.</summary>
public sealed record UpsDetail(UpsSummary Summary, DateTimeOffset? ConnectedSince, IReadOnlyDictionary<string, string> Variables);
