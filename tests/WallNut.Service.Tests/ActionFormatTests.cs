using System.Text.Json;
using WallNut.Service.Actions;
using WallNut.Service.Configuration;
using WallNut.Shared;
using WallNut.Shared.Actions;

namespace WallNut.Service.Tests;

public class ActionFormatTests
{
    private static ActionMessage Msg(string kind = "OnBattery", NotificationUrgency urgency = NotificationUrgency.Critical) => new()
    {
        Kind = kind, Title = "Power failure: NAS \"main\"", Message = "Running on battery (battery 97%).",
        Urgency = urgency, IsProblem = true, Time = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
        Machine = "PC1", UpsId = "nas", UpsName = "NAS", Host = "10.0.0.5", Status = "OB DISCHRG", Charge = 97, RuntimeSeconds = 1500, Load = 21.5,
    };

    [Fact]
    public void Generic_json_has_the_event_and_ups()
    {
        var (body, type) = WebhookSender.Body(new WebhookOptions(), Msg());
        Assert.Equal("application/json", type);
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        Assert.Equal("wallnut", r.GetProperty("source").GetString());
        Assert.Equal("OnBattery", r.GetProperty("event").GetString());
        Assert.True(r.GetProperty("problem").GetBoolean());
        Assert.Equal("critical", r.GetProperty("urgency").GetString());
        Assert.Equal(97, r.GetProperty("ups").GetProperty("charge").GetDouble());
        Assert.Equal("OB DISCHRG", r.GetProperty("ups").GetProperty("status").GetString());
    }

    [Fact]
    public void Discord_slack_and_teams_bodies()
    {
        using var discord = JsonDocument.Parse(WebhookSender.Body(new WebhookOptions { Preset = WebhookPresets.Discord }, Msg()).Body);
        var embed = discord.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Power failure: NAS \"main\"", embed.GetProperty("title").GetString());
        Assert.Equal(0xE5484D, embed.GetProperty("color").GetInt32());

        using var slack = JsonDocument.Parse(WebhookSender.Body(new WebhookOptions { Preset = WebhookPresets.Slack }, Msg()).Body);
        Assert.StartsWith("*Power failure", slack.RootElement.GetProperty("text").GetString());

        using var teams = JsonDocument.Parse(WebhookSender.Body(new WebhookOptions { Preset = WebhookPresets.Teams }, Msg()).Body);
        var card = teams.RootElement.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("Attention", card.GetProperty("body")[0].GetProperty("color").GetString());
    }

    [Fact]
    public void Custom_template_fills_placeholders_and_escapes_json()
    {
        var w = new WebhookOptions
        {
            Preset = WebhookPresets.Custom,
            Body = """{"text":"{title} at {charge}% on {machine}","unknown":"{nope}"}""",
        };
        var (body, _) = WebhookSender.Body(w, Msg());
        using var doc = JsonDocument.Parse(body); // still valid JSON despite the quotes in the title
        Assert.Equal("Power failure: NAS \"main\" at 97% on PC1", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("{nope}", doc.RootElement.GetProperty("unknown").GetString());
    }

    [Fact]
    public void Plain_text_template_is_not_escaped()
    {
        var w = new WebhookOptions { Preset = WebhookPresets.Custom, ContentType = "text/plain", Body = "{title}" };
        Assert.Equal("Power failure: NAS \"main\"", WebhookSender.Body(w, Msg()).Body);
    }

    [Fact]
    public void Signature_is_hmac_of_timestamp_and_body()
    {
        var sig = WebhookSender.Sign("s3cret", "1700000000", "{}");
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("s3cret"u8, "1700000000.{}"u8));
        Assert.Equal(expected, sig);
    }

    [Theory]
    [InlineData("ShutdownInitiated", 5)]
    [InlineData("LowBattery", 5)]
    [InlineData("OnBattery", 4)]
    [InlineData("CommunicationLost", 4)]
    [InlineData("OnLine", 3)]
    public void Ntfy_priority(string kind, int priority) => Assert.Equal(priority, NtfySender.Priority(Msg(kind)));

    [Fact]
    public void Ntfy_tags_become_emoji() => Assert.Equal(["zap"], NtfySender.Tags(Msg("OnBattery")));

    [Fact]
    public void Validation_explains_problems()
    {
        Assert.Contains(ActionValidation.Validate("p", new ActionOptions { Ntfy = new NtfyOptions { Topic = "bad topic!" } }), p => p.Contains("topic"));
        Assert.Contains(ActionValidation.Validate("w", new ActionOptions { Type = ActionTypes.Webhook, Webhook = new WebhookOptions { Url = "ftp://x" } }), p => p.Contains("http"));
        Assert.Contains(ActionValidation.Validate("e", new ActionOptions { Type = ActionTypes.Email, Email = new EmailOptions { Host = "smtp.x", From = "nope" } }), p => p.Contains("From"));
        Assert.Contains(ActionValidation.Validate("s", new ActionOptions { Type = ActionTypes.Script, Script = new ScriptOptions { Path = "relative.ps1" } }), p => p.Contains("full path"));
        Assert.Empty(ActionValidation.Validate("ok", new ActionOptions { Ntfy = new NtfyOptions { Topic = "wallnut-abc123" } }));
    }
}
