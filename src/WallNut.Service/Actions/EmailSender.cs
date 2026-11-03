using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WallNut.Service.Configuration;
using WallNut.Shared;
using WallNut.Shared.Actions;

namespace WallNut.Service.Actions;

/// <summary>Email alerts through an SMTP server with a user name and (app) password.</summary>
public sealed class EmailSender : IActionSender
{
    public string Type => ActionTypes.Email;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<ActionResult> SendAsync(ActionOptions action, ActionMessage m, CancellationToken ct)
    {
        var e = action.Email ?? throw new InvalidOperationException("Email settings are missing.");
        var mail = Build(e, m);

        using var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await smtp.ConnectAsync(e.Host, e.Port, Security(e.Security), ct);
            if (!string.IsNullOrEmpty(e.Password))
                await smtp.AuthenticateAsync(e.Username ?? MailboxAddress.Parse(e.From).Address, e.Password, ct);
            await smtp.SendAsync(mail, ct);
            await smtp.DisconnectAsync(true, ct);
            return ActionResult.Success($"sent to {string.Join(", ", e.To)}");
        }
        catch (AuthenticationException ex)
        {
            var hint = EmailPresets.ForHost(e.Host).Id switch
            {
                "Gmail" => " Gmail needs an app password (Google Account > Security > App passwords), not your normal password.",
                "Outlook" => " Microsoft may have turned off password sign-in for this account.",
                "iCloud" or "Yahoo" or "Fastmail" => " This provider needs an app password, not your normal password.",
                _ => "",
            };
            return ActionResult.Failed($"the mail server refused the user name or password ({ex.Message.TrimEnd('.')}).{hint}", retry: false);
        }
        catch (SslHandshakeException)
        {
            return ActionResult.Failed($"couldn't set up a secure connection to {e.Host}:{e.Port}. Check the port and security setting (587 uses STARTTLS, 465 uses SSL).", retry: false);
        }
        catch (SmtpCommandException ex)
        {
            // 4xx means "try again later"; 5xx means it will never work as is.
            var temporary = (int)ex.StatusCode is >= 400 and < 500;
            var who = ex.Mailbox is { } box ? $" for {box.Address}" : "";
            return ActionResult.Failed($"the mail server said no{who}: {ex.Message.TrimEnd('.')} ({(int)ex.StatusCode}).", retry: temporary);
        }
        catch (SmtpProtocolException ex) { return ActionResult.Failed($"the mail server misbehaved: {ex.Message}", retry: true); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            return ActionResult.Failed($"couldn't find the mail server {e.Host}.", retry: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ServiceNotConnectedException)
        {
            return ActionResult.Failed($"couldn't reach {e.Host}:{e.Port}: {ex.Message}", retry: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ActionResult.Failed($"{e.Host} took too long to answer.", retry: true);
        }
    }

    private static SecureSocketOptions Security(string s) => s switch
    {
        "SslOnConnect" => SecureSocketOptions.SslOnConnect,
        "None" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

    /// <summary>The email for an event: a short subject, plain text, and a simple HTML version.</summary>
    public static MimeMessage Build(EmailOptions e, ActionMessage m)
    {
        var from = MailboxAddress.Parse(e.From);
        if (string.IsNullOrEmpty(from.Name)) from.Name = "WALLNut";
        var mail = new MimeMessage();
        mail.From.Add(from);
        foreach (var to in e.To) mail.To.Add(MailboxAddress.Parse(to));
        var rehearsal = m.TestMode && m.Kind == "ShutdownInitiated";
        mail.Subject = "[WALLNut] " + m.Title + (rehearsal ? " (test mode)" : "");
        if (m.Urgency == NotificationUrgency.Critical) mail.Importance = MessageImportance.High;

        var details = Details(m);
        var text = m.Message + "\n\n" + string.Join("\n", details.Select(d => $"{d.Label}: {d.Value}")) +
                   "\n\n-- \nSent by WALLNut on " + m.Machine + ".";
        var color = m.Urgency == NotificationUrgency.Critical ? "#c0392b" : m.IsProblem ? "#d68910" : "#1e8449";
        var rows = string.Concat(details.Select(d =>
            $"<tr><td style=\"padding:3px 14px 3px 0;color:#666\">{H(d.Label)}</td><td style=\"padding:3px 0\">{H(d.Value)}</td></tr>"));
        var html = $"""
            <div style="font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;color:#222;max-width:560px">
              <div style="border-left:4px solid {color};padding:6px 12px;margin-bottom:14px">
                <div style="font-size:17px;font-weight:600">{H(m.Title)}{(rehearsal ? " (test mode)" : "")}</div>
                <div style="margin-top:4px">{H(m.Message)}</div>
              </div>
              <table style="border-collapse:collapse;font-size:13px">{rows}</table>
              <p style="color:#888;font-size:12px;margin-top:18px">Sent by WALLNut on {H(m.Machine)}.</p>
            </div>
            """;
        mail.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        return mail;
    }

    private static List<(string Label, string Value)> Details(ActionMessage m)
    {
        var d = new List<(string, string)>();
        if (m.UpsName is { Length: > 0 }) d.Add(("UPS", m.UpsName + (m.Host is { Length: > 0 } ? $" ({m.Host})" : "")));
        if (m.Status is { Length: > 0 }) d.Add(("Status", $"{DescribeStatus(m.Status)} ({m.Status})"));
        if (m.Charge is { } c) d.Add(("Battery", $"{c:0}%"));
        if (m.RuntimeSeconds is { } r) d.Add(("Runtime left", r >= 3600 ? $"{r / 3600:0.#} h" : $"{r / 60:0} min"));
        if (m.Load is { } l) d.Add(("Load", $"{l:0}%"));
        d.Add(("Computer", m.Machine));
        d.Add(("Time", m.Time.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)));
        return d;
    }

    private static string DescribeStatus(string raw)
    {
        var st = Nut.UpsStatus.Parse(raw);
        if (st.IsForcedShutdown) return "Forced shutdown";
        if (st.IsOnBattery) return st.IsLowBattery ? "On battery, low" : "On battery";
        if (st.Has(Nut.UpsStatusFlags.OnLine)) return st.IsLowBattery ? "Online, battery low" : "Online";
        return "Unknown";
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);
}
