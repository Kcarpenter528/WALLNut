using WallNut.Shared.Actions;

namespace WallNut.Service.Configuration;

/// <summary>One alert/action in WallNut:Actions (see <see cref="ActionConfig"/> for what each setting means).</summary>
public sealed class ActionOptions
{
    public string? Name { get; set; }
    public string Type { get; set; } = ActionTypes.Ntfy;
    public bool Enabled { get; set; } = true;

    /// <summary>Null means the defaults (<see cref="ActionDefaults.Events"/>).</summary>
    public List<string>? Events { get; set; }
    public List<string> Ups { get; set; } = [];
    public double DelaySeconds { get; set; }

    public NtfyOptions? Ntfy { get; set; }
    public WebhookOptions? Webhook { get; set; }
    public ScriptOptions? Script { get; set; }
    public EmailOptions? Email { get; set; }

    public IReadOnlyList<string> EffectiveEvents => Events ?? ActionDefaults.Events;
}

public sealed class NtfyOptions
{
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public string? Token { get; set; }
}

public sealed class WebhookOptions
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Preset { get; set; } = WebhookPresets.Generic;
    public string? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Secret { get; set; }
}

public sealed class ScriptOptions
{
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public double TimeoutSeconds { get; set; } = 60;
    public bool RunBeforeShutdown { get; set; }
    public bool RunInTestMode { get; set; }
}

public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
}

public static class ActionValidation
{
    /// <summary>Problems with one action, in plain words. Empty when valid.</summary>
    public static List<string> Validate(string id, ActionOptions a)
    {
        var p = new List<string>();
        string Where = $"Action '{a.Name ?? id}'";
        if (!ActionTypes.All.Contains(a.Type)) { p.Add($"{Where}: unknown type '{a.Type}'."); return p; }
        if (a.DelaySeconds is < 0 or > 86400) p.Add($"{Where}: the delay must be between 0 and 86400 seconds.");
        if (a.Events is { Count: 0 }) p.Add($"{Where}: choose at least one event to react to.");

        switch (a.Type)
        {
            case ActionTypes.Ntfy:
                if (a.Ntfy is not { } n) { p.Add($"{Where}: ntfy settings are missing."); break; }
                if (!IsHttpUrl(n.Server)) p.Add($"{Where}: the ntfy server must be an http(s) address, e.g. https://ntfy.sh.");
                if (string.IsNullOrWhiteSpace(n.Topic) || n.Topic.Length > 64 || !n.Topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    p.Add($"{Where}: the topic must be 1-64 letters, digits, '-' or '_'.");
                break;
            case ActionTypes.Webhook:
                if (a.Webhook is not { } w) { p.Add($"{Where}: webhook settings are missing."); break; }
                if (!IsHttpUrl(w.Url)) p.Add($"{Where}: the webhook URL must start with http:// or https://.");
                if (w.Method is not ("POST" or "PUT" or "GET")) p.Add($"{Where}: the method must be POST, PUT or GET.");
                if (!WebhookPresets.All.Contains(w.Preset)) p.Add($"{Where}: unknown preset '{w.Preset}'.");
                if (w.Preset == WebhookPresets.Custom && string.IsNullOrWhiteSpace(w.Body) && w.Method != "GET")
                    p.Add($"{Where}: a custom webhook needs a body.");
                break;
            case ActionTypes.Script:
                if (a.Script is not { } s) { p.Add($"{Where}: script settings are missing."); break; }
                if (string.IsNullOrWhiteSpace(s.Path) || !System.IO.Path.IsPathFullyQualified(s.Path))
                    p.Add($"{Where}: give the script's full path.");
                else if (!File.Exists(s.Path)) p.Add($"{Where}: the script {s.Path} doesn't exist.");
                if (s.TimeoutSeconds is < 1 or > 3600) p.Add($"{Where}: the time limit must be between 1 and 3600 seconds.");
                break;
            case ActionTypes.Email:
                if (a.Email is not { } e) { p.Add($"{Where}: email settings are missing."); break; }
                if (string.IsNullOrWhiteSpace(e.Host)) p.Add($"{Where}: enter the mail server (SMTP host).");
                if (e.Port is < 1 or > 65535) p.Add($"{Where}: the mail server port isn't valid.");
                if (e.Security is not ("StartTls" or "SslOnConnect" or "None")) p.Add($"{Where}: security must be StartTls, SslOnConnect or None.");
                if (!IsEmail(e.From)) p.Add($"{Where}: the From address isn't a valid email address.");
                if (e.To.Count == 0 || !e.To.All(IsEmail)) p.Add($"{Where}: enter at least one valid recipient.");
                if (e.Security == "None" && !string.IsNullOrEmpty(e.Password) && !IsLocal(e.Host))
                    p.Add($"{Where}: the password would be sent unencrypted. Choose STARTTLS or SSL, or use a mail server on this computer.");
                break;
        }
        return p;
    }

    private static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool IsLocal(string host) =>
        host.Trim() is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private static bool IsEmail(string? s) => System.Net.Mail.MailAddress.TryCreate(s, out _);
}
