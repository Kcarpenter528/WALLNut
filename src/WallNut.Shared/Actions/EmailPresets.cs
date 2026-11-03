namespace WallNut.Shared.Actions;

/// <summary>Mail server settings for common email providers, so people don't have to look them up.</summary>
public sealed record EmailPreset(string Id, string Name, string Host, int Port, string Security, string Help);

public static class EmailPresets
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<EmailPreset> All =
    [
        new("Gmail", "Gmail", "smtp.gmail.com", 587, "StartTls",
            "Gmail needs an app password, not your normal one: Google Account > Security > 2-Step Verification > App passwords. The user name is your Gmail address."),
        new("Outlook", "Outlook.com / Microsoft 365", "smtp-mail.outlook.com", 587, "StartTls",
            "Microsoft is phasing out password sign-in for sending mail, so this may be refused. If it is, use another provider for now; Microsoft sign-in is planned."),
        new("iCloud", "iCloud Mail", "smtp.mail.me.com", 587, "StartTls",
            "Use an app-specific password from account.apple.com > Sign-In and Security. The user name and From address are your iCloud address."),
        new("Yahoo", "Yahoo Mail", "smtp.mail.yahoo.com", 465, "SslOnConnect",
            "Use an app password: Yahoo Account Info > Account Security > Generate app password."),
        new("Fastmail", "Fastmail", "smtp.fastmail.com", 465, "SslOnConnect",
            "Use an app password: Settings > Privacy & Security > Integrations > App passwords."),
        new(Custom, "Other mail server", "", 587, "StartTls",
            "Your provider's SMTP settings, or a mail server on your network. Port 587 with STARTTLS is the usual choice; 465 uses SSL from the start."),
    ];

    /// <summary>The preset whose server matches, or Custom.</summary>
    public static EmailPreset ForHost(string? host) =>
        All.FirstOrDefault(p => p.Host.Length > 0 && string.Equals(p.Host, host?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[^1];

    public static string DescribeSecurity(string security) => security switch
    {
        "StartTls" => "STARTTLS (usually port 587)",
        "SslOnConnect" => "SSL/TLS (usually port 465)",
        "None" => "None (only for a mail server on your own network)",
        _ => security,
    };

    public static readonly IReadOnlyList<string> SecurityOptions = ["StartTls", "SslOnConnect", "None"];
}
