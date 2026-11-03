namespace WallNut.Shared.Actions;

/// <summary>The kinds of action WALLNut can run when something happens.</summary>
public static class ActionTypes
{
    public const string Ntfy = "Ntfy";
    public const string Webhook = "Webhook";
    public const string Script = "Script";
    public const string Email = "Email";

    public static readonly IReadOnlyList<string> All = [Ntfy, Webhook, Script, Email];

    public static string Describe(string type) => type switch
    {
        Ntfy => "Phone alert (ntfy)",
        Webhook => "Webhook",
        Script => "Run a script",
        Email => "Email",
        _ => type,
    };
}

/// <summary>
/// Something the service does when an event happens: send a phone alert, call a webhook, run a
/// script or send an email. Secrets (tokens, passwords, header values) are never sent back to apps:
/// the matching "...Set" flag says whether one is stored, and null keeps it when saving.
/// </summary>
public sealed record ActionConfig
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string Type { get; init; } = ActionTypes.Ntfy;
    public bool Enabled { get; init; } = true;

    /// <summary>Event kinds that trigger it (names from <see cref="NotificationCatalog"/>).</summary>
    public IReadOnlyList<string> Events { get; init; } = ActionDefaults.Events;

    /// <summary>UPS endpoint ids it applies to; empty = all. Events about this computer always apply.</summary>
    public IReadOnlyList<string> Ups { get; init; } = [];

    /// <summary>
    /// Wait this long before acting on a problem (power failure, lost contact, overload...). If it
    /// clears in the meantime, nothing happens. 0 = act immediately.
    /// </summary>
    public double DelaySeconds { get; init; }

    public NtfyConfig? Ntfy { get; init; }
    public WebhookConfig? Webhook { get; init; }
    public ScriptConfig? Script { get; init; }
    public EmailConfig? Email { get; init; }
}

public sealed record NtfyConfig
{
    public string Server { get; init; } = "https://ntfy.sh";
    public string Topic { get; init; } = "";
    /// <summary>Access token for protected topics. Write-only.</summary>
    public string? Token { get; init; }
    public bool TokenSet { get; init; }
}

public static class WebhookPresets
{
    public const string Generic = "Generic";
    public const string Discord = "Discord";
    public const string Slack = "Slack";
    public const string Teams = "Teams";
    public const string HomeAssistant = "HomeAssistant";
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<string> All = [Generic, Discord, Slack, Teams, HomeAssistant, Custom];

    public static string Describe(string preset) => preset switch
    {
        Generic => "Generic JSON",
        Discord => "Discord",
        Slack => "Slack",
        Teams => "Microsoft Teams (Workflows)",
        HomeAssistant => "Home Assistant",
        Custom => "Custom body",
        _ => preset,
    };
}

public sealed record WebhookConfig
{
    public string Url { get; init; } = "";
    public string Method { get; init; } = "POST";
    public string Preset { get; init; } = WebhookPresets.Generic;

    /// <summary>For the Custom preset: the body, with placeholders such as {event}, {ups}, {charge}, {message}.</summary>
    public string? Body { get; init; }
    public string ContentType { get; init; } = "application/json";

    /// <summary>Extra headers. Values are write-only: they come back empty, and an empty value keeps the stored one.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Signs each request (X-WallNut-Signature: sha256=HMAC of the body). Write-only.</summary>
    public string? Secret { get; init; }
    public bool SecretSet { get; init; }
}

public sealed record ScriptConfig
{
    /// <summary>A .ps1, .bat, .cmd, .sh or executable file. Must not be changeable by non-administrators.</summary>
    public string Path { get; init; } = "";
    public string? Arguments { get; init; }
    public double TimeoutSeconds { get; init; } = 60;

    /// <summary>When this computer is about to shut down, run it and wait for it (up to the timeout) first.</summary>
    public bool RunBeforeShutdown { get; init; }

    /// <summary>Also run it during shutdown rehearsals (test mode). Off by default so a rehearsal can't stop real things.</summary>
    public bool RunInTestMode { get; init; }
}

public sealed record EmailConfig
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    /// <summary>"StartTls", "SslOnConnect" or "None".</summary>
    public string Security { get; init; } = "StartTls";
    public string? Username { get; init; }
    /// <summary>Write-only.</summary>
    public string? Password { get; init; }
    public bool PasswordSet { get; init; }
    public string From { get; init; } = "";
    public IReadOnlyList<string> To { get; init; } = [];
}

public sealed record ActionTestResult(bool Ok, string Message);

public static class ActionDefaults
{
    /// <summary>What a new action reacts to: the things worth being told about away from the computer.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "OnBattery", "OnLine", "LowBattery", "ForcedShutdown", "ShutdownInitiated", "ShutdownFailed",
        "CommunicationLost", "CommunicationRestored", "Overload", "ReplaceBattery", "Off",
    ];

    /// <summary>Events that never trigger actions (they'd only cause noise or loops).</summary>
    public static readonly IReadOnlySet<string> Never = new HashSet<string> { "Test", "ActionFailed" };
}
