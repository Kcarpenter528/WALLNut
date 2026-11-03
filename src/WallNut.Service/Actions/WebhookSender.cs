using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WallNut.Service.Configuration;
using WallNut.Shared.Actions;

namespace WallNut.Service.Actions;

/// <summary>
/// Calls a URL when something happens: generic JSON, or ready-made bodies for Discord, Slack,
/// Microsoft Teams and Home Assistant, or a custom template. Optionally signed with HMAC-SHA256.
/// </summary>
public sealed partial class WebhookSender(IHttpClientFactory http) : IActionSender
{
    public string Type => ActionTypes.Webhook;

    public async Task<ActionResult> SendAsync(ActionOptions action, ActionMessage m, CancellationToken ct)
    {
        var w = action.Webhook!;
        var (body, contentType) = Body(w, m);
        using var request = new HttpRequestMessage(new HttpMethod(w.Method), w.Url);
        if (w.Method != "GET")
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType + "; charset=utf-8");
        }
        foreach (var (name, value) in w.Headers)
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);

        if (!string.IsNullOrEmpty(w.Secret))
        {
            // Receivers verify: HMAC-SHA256(secret, timestamp + "." + body) == signature.
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("X-WallNut-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-WallNut-Signature", "sha256=" + Sign(w.Secret, timestamp, body));
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "WALLNut");
        return await HttpDelivery.SendAsync(http, request, "the webhook", ct);
    }

    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));

    /// <summary>The request body and content type for the webhook's preset.</summary>
    public static (string Body, string ContentType) Body(WebhookOptions w, ActionMessage m)
    {
        var color = m.Urgency == Shared.NotificationUrgency.Critical ? 0xE5484D : m.IsProblem ? 0xF5A524 : 0x30A46C;
        switch (w.Preset)
        {
            case WebhookPresets.Discord:
                return (new JsonObject
                {
                    ["username"] = "WALLNut",
                    ["embeds"] = new JsonArray(new JsonObject
                    {
                        ["title"] = m.Title,
                        ["description"] = m.Message,
                        ["color"] = color,
                        ["timestamp"] = m.Time.ToString("o", CultureInfo.InvariantCulture),
                        ["footer"] = new JsonObject { ["text"] = m.Machine },
                    }),
                }.ToJsonString(), "application/json");

            case WebhookPresets.Slack:
                return (new JsonObject { ["text"] = $"*{m.Title}*\n{m.Message}" }.ToJsonString(), "application/json");

            case WebhookPresets.Teams:
                // A Teams "Workflows" webhook takes an Adaptive Card.
                return (new JsonObject
                {
                    ["type"] = "message",
                    ["attachments"] = new JsonArray(new JsonObject
                    {
                        ["contentType"] = "application/vnd.microsoft.card.adaptive",
                        ["content"] = new JsonObject
                        {
                            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                            ["type"] = "AdaptiveCard",
                            ["version"] = "1.4",
                            ["body"] = new JsonArray(
                                new JsonObject { ["type"] = "TextBlock", ["text"] = m.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                                    ["color"] = m.Urgency == Shared.NotificationUrgency.Critical ? "Attention" : m.IsProblem ? "Warning" : "Good" },
                                new JsonObject { ["type"] = "TextBlock", ["text"] = m.Message, ["wrap"] = true },
                                new JsonObject { ["type"] = "TextBlock", ["text"] = $"{m.Machine} · {m.Time.ToLocalTime():g}", ["isSubtle"] = true, ["size"] = "Small" }),
                        },
                    }),
                }.ToJsonString(), "application/json");

            case WebhookPresets.Custom:
                return (Fill(w.Body ?? "", m, json: w.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)), w.ContentType);

            default: // Generic and Home Assistant: WALLNut's own JSON.
                return (Generic(m).ToJsonString(), "application/json");
        }
    }

    /// <summary>WALLNut's standard webhook JSON.</summary>
    public static JsonObject Generic(ActionMessage m) => new()
    {
        ["source"] = "wallnut",
        ["event"] = m.Kind,
        ["title"] = m.Title,
        ["message"] = m.Message,
        ["problem"] = m.IsProblem,
        ["urgency"] = m.Urgency.ToString().ToLowerInvariant(),
        ["time"] = m.Time.ToString("o", CultureInfo.InvariantCulture),
        ["machine"] = m.Machine,
        ["test_mode"] = m.TestMode,
        ["ups"] = m.UpsId is null ? null : new JsonObject
        {
            ["id"] = m.UpsId,
            ["name"] = m.UpsName,
            ["host"] = m.Host,
            ["status"] = m.Status,
            ["charge"] = m.Charge,
            ["runtime"] = m.RuntimeSeconds,
            ["load"] = m.Load,
        },
    };

    /// <summary>Replaces {placeholders}; values are JSON-escaped when the body is JSON. Unknown ones are left alone.</summary>
    public static string Fill(string template, ActionMessage m, bool json)
    {
        var fields = m.Fields;
        return Placeholder().Replace(template, match =>
        {
            if (!fields.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            return json ? JsonSerializer.Serialize(value)[1..^1] : value;
        });
    }

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Placeholder();
}
