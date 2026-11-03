using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using WallNut.Service.Actions;
using WallNut.Service.Configuration;
using WallNut.Shared;
using WallNut.Shared.Actions;

namespace WallNut.Service.Tests;

/// <summary>A tiny SMTP server: just enough of the protocol for a mail client to log in and send.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    public sealed record Mail(string? User, string From, IReadOnlyList<string> To, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public ConcurrentQueue<Mail> Received { get; } = new();
    public string User { get; init; } = "ups@example.com";
    public string Password { get; init; } = "app-password";
    /// <summary>Reply to MAIL FROM with this (e.g. "451 try later") instead of accepting.</summary>
    public string? RejectMail { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            // Stopping the listener ends a pending accept with any of these, depending on timing.
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        string? user = null, from = null;
        var to = new List<string>();
        await writer.WriteLineAsync("220 fake.example ESMTP");
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.example\r\n250-AUTH PLAIN\r\n250 8BITMIME");
                        break;
                    case "AUTH":
                        var parts = line.Split(' ');
                        var b64 = parts.Length > 2 ? parts[2] : null;
                        if (b64 is null) { await writer.WriteLineAsync("334 "); b64 = await reader.ReadLineAsync(); }
                        var fields = Encoding.UTF8.GetString(Convert.FromBase64String(b64!)).Split('\0');
                        if (fields.Length == 3 && fields[1] == User && fields[2] == Password) { user = fields[1]; await writer.WriteLineAsync("235 2.7.0 OK"); }
                        else await writer.WriteLineAsync("535 5.7.8 Username and Password not accepted");
                        break;
                    case "MAIL":
                        if (RejectMail is { } reject) { await writer.WriteLineAsync(reject); break; }
                        from = Address(line); to.Clear();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        to.Add(Address(line));
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d.StartsWith("..") ? d[1..] : d);
                        Received.Enqueue(new Mail(user, from ?? "", to.ToList(), data.ToString()));
                        await writer.WriteLineAsync("250 2.0.0 Queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string Address(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        return start >= 0 && end > start ? line[(start + 1)..end] : "";
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
    }
}

public sealed class EmailActionTests : IAsyncDisposable
{
    private readonly FakeSmtpServer _smtp = new();

    public ValueTask DisposeAsync() => _smtp.DisposeAsync();

    private ActionOptions Action(string? password = "app-password", string? user = null) => new()
    {
        Name = "Email me", Type = ActionTypes.Email,
        Email = new EmailOptions
        {
            Host = "127.0.0.1", Port = _smtp.Port, Security = "None", Username = user, Password = password,
            From = "ups@example.com", To = ["me@example.com", "partner@example.com"],
        },
    };

    private static ActionMessage PowerFailure() => ActionMessage.Test() with
    {
        Kind = "OnBattery", Title = "Power failure: Synology NAS", Message = "Synology NAS is on battery.",
        Urgency = NotificationUrgency.Critical, IsProblem = true, UpsName = "Synology NAS", Host = "nas.local",
        Status = "OB DISCHRG", Charge = 87, RuntimeSeconds = 1260, Load = 31,
    };

    [Fact]
    public async Task Sends_to_every_recipient_after_logging_in()
    {
        var result = await new EmailSender().SendAsync(Action(), PowerFailure(), default);

        Assert.True(result.Ok, result.Message);
        var mail = Assert.Single(_smtp.Received);
        Assert.Equal("ups@example.com", mail.User); // the From address is the user name when none is given
        Assert.Equal("ups@example.com", mail.From);
        Assert.Equal(["me@example.com", "partner@example.com"], mail.To);

        var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(mail.Data)));
        Assert.Equal("[WALLNut] Power failure: Synology NAS", message.Subject);
        Assert.Equal(MessageImportance.High, message.Importance);
        Assert.Equal("WALLNut", message.From.Mailboxes.Single().Name);
        Assert.Contains("Synology NAS is on battery.", message.TextBody);
        Assert.Contains("Battery: 87%", message.TextBody);
        Assert.Contains("Runtime left: 21 min", message.TextBody);
        Assert.Contains("On battery (OB DISCHRG)", message.TextBody);
        Assert.Contains("<table", message.HtmlBody);
    }

    [Fact]
    public async Task Wrong_password_is_explained_and_not_retried()
    {
        var result = await new EmailSender().SendAsync(Action(password: "my-normal-password"), PowerFailure(), default);
        Assert.False(result.Ok);
        Assert.False(result.Retry);
        Assert.Contains("user name or password", result.Message);
        Assert.Empty(_smtp.Received);
    }

    [Fact]
    public async Task Temporary_refusal_is_retried_but_permanent_is_not()
    {
        _smtp.RejectMail = "451 4.3.0 Try again later";
        var temporary = await new EmailSender().SendAsync(Action(), PowerFailure(), default);
        Assert.False(temporary.Ok);
        Assert.True(temporary.Retry);

        _smtp.RejectMail = "550 5.7.1 Sender not allowed";
        var permanent = await new EmailSender().SendAsync(Action(), PowerFailure(), default);
        Assert.False(permanent.Ok);
        Assert.False(permanent.Retry);
        Assert.Contains("Sender not allowed", permanent.Message);
    }

    [Fact]
    public async Task Unreachable_server_is_retried()
    {
        var a = Action();
        a.Email!.Port = 9; // nothing listens there
        var result = await new EmailSender { Timeout = TimeSpan.FromSeconds(5) }.SendAsync(a, PowerFailure(), default);
        Assert.False(result.Ok);
        Assert.True(result.Retry);
    }

    [Fact]
    public void Rehearsal_is_marked_in_the_subject()
    {
        var m = ActionMessage.Test() with { Kind = "ShutdownInitiated", Title = "Shutting down", TestMode = true };
        Assert.EndsWith("(test mode)", EmailSender.Build(Action().Email!, m).Subject);
    }

    [Fact]
    public void Password_never_goes_unencrypted_to_another_computer()
    {
        var a = Action();
        a.Email!.Host = "smtp.example.com";
        Assert.Contains(ActionValidation.Validate("mail", a), p => p.Contains("unencrypted"));
        a.Email.Security = "StartTls";
        Assert.Empty(ActionValidation.Validate("mail", a));
        a.Email.To = ["not an address"];
        Assert.Contains(ActionValidation.Validate("mail", a), p => p.Contains("recipient"));
    }

    [Fact]
    public void Presets_are_found_by_server_name()
    {
        Assert.Equal("Gmail", EmailPresets.ForHost("SMTP.gmail.com").Id);
        Assert.Equal(EmailPresets.Custom, EmailPresets.ForHost("mail.myisp.net").Id);
    }
}
