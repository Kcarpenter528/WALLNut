using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using WallNut.Service.Configuration;
using WallNut.Shared.Actions;

namespace WallNut.Service.Actions;

/// <summary>
/// Phone alerts through ntfy (https://ntfy.sh or a self-hosted server). Uses ntfy's JSON publishing,
/// so titles and messages can contain any characters.
/// </summary>
public sealed class NtfySender(IHttpClientFactory http) : IActionSender
{
    public string Type => ActionTypes.Ntfy;

    public async Task<ActionResult> SendAsync(ActionOptions action, ActionMessage m, CancellationToken ct)
    {
        var n = action.Ntfy!;
        var body = new JsonObject
        {
            ["topic"] = n.Topic,
            ["title"] = m.Title + (m.TestMode && m.Kind is "ShutdownInitiated" ? " (test mode)" : ""),
            ["message"] = m.Message,
            ["priority"] = Priority(m),
            ["tags"] = new JsonArray(Tags(m).Select(t => (JsonNode?)t).ToArray()),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, n.Server.TrimEnd('/') + "/")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrEmpty(n.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", n.Token);
        return await HttpDelivery.SendAsync(http, request, "ntfy", ct);
    }

    /// <summary>ntfy priority 1-5. 5 can break through Do Not Disturb (if the user allows it).</summary>
    public static int Priority(ActionMessage m) => m.Kind switch
    {
        "ShutdownInitiated" or "ShutdownFailed" or "ForcedShutdown" or "LowBattery" or "Off" => 5,
        "OnBattery" or "Overload" or "CommunicationLost" or "ActionFailed" => 4,
        _ when m.Urgency == Shared.NotificationUrgency.Low => 2,
        _ => 3,
    };

    /// <summary>ntfy turns these into emoji in front of the title.</summary>
    public static string[] Tags(ActionMessage m) => m.Kind switch
    {
        "OnBattery" => ["zap"],
        "OnLine" => ["electric_plug"],
        "LowBattery" => ["battery", "warning"],
        "ShutdownInitiated" or "ForcedShutdown" => ["rotating_light"],
        "ShutdownFailed" => ["x", "rotating_light"],
        "CommunicationLost" or "DataStale" => ["warning"],
        "CommunicationRestored" or "DataRestored" => ["white_check_mark"],
        "Overload" => ["warning"],
        "ReplaceBattery" => ["battery"],
        "Off" => ["no_entry"],
        "Test" => ["white_check_mark"],
        _ => ["information_source"],
    };
}

/// <summary>Shared HTTP sending: timeouts, and which failures are worth retrying.</summary>
internal static class HttpDelivery
{
    public static async Task<ActionResult> SendAsync(IHttpClientFactory factory, HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = factory.CreateClient("actions");
            using var reply = await client.SendAsync(request, cts.Token);
            if (reply.IsSuccessStatusCode) return ActionResult.Success($"Sent to {what} ({(int)reply.StatusCode}).");

            var detail = (await reply.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (detail.Length > 300) detail = detail[..300] + "...";
            var retry = reply.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)reply.StatusCode >= 500;
            var hint = reply.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " Check the token or credentials.",
                HttpStatusCode.NotFound => " Check the address.",
                _ => "",
            };
            return ActionResult.Failed($"{what} answered {(int)reply.StatusCode} {reply.ReasonPhrase}.{hint}" + (detail.Length > 0 ? $" ({detail})" : ""), retry);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ActionResult.Failed($"{what} didn't answer within 20 seconds.", retry: true);
        }
        catch (HttpRequestException e)
        {
            return ActionResult.Failed($"Couldn't reach {what}: {e.Message} Is this computer online?", retry: true);
        }
    }
}
