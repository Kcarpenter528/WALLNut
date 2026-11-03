using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WallNut.Service.Actions;
using WallNut.Service.Configuration;
using WallNut.Shared.Actions;
using WallNut.Shared.Settings;

namespace WallNut.Service.Api;

/// <summary>Alerts and actions on the local API. Reading is open; changing and testing need an administrator.</summary>
public static class ActionsApi
{
    private static readonly JsonSerializerOptions Write = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapActionsApi(this RouteGroupBuilder api)
    {
        api.MapPut("/config/actions/{id}", (string id, ActionConfig body, HttpContext http, ICallerIdentity callers,
            IOptionsMonitor<WallNutOptions> options, MachineConfigStatus file) =>
        {
            if (!callers.Get(http).IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (file.Path is null) return Problems(["This service was started without a settings file, so changes can't be saved."]);
            if (string.IsNullOrWhiteSpace(id) || id.Length > 40 || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
                return Problems(["The id must be 1-40 letters, digits, '-' or '_' (for example \"phone\")."]);

            var action = FromConfig(body, options.CurrentValue.Actions.GetValueOrDefault(id));
            if (ActionValidation.Validate(id, action) is { Count: > 0 } problems) return Problems(problems);

            new MachineConfigWriter(file.Path).Update(w =>
            {
                var actions = w["Actions"] as JsonObject ?? (JsonObject)(w["Actions"] = new JsonObject());
                foreach (var key in actions.Select(kv => kv.Key).Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList())
                    actions.Remove(key);
                actions[id] = JsonSerializer.SerializeToNode(action, Write);
            });
            return Results.NoContent();
        });

        api.MapDelete("/config/actions/{id}", (string id, HttpContext http, ICallerIdentity callers, MachineConfigStatus file) =>
        {
            if (!callers.Get(http).IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (file.Path is null) return Problems(["This service was started without a settings file, so changes can't be saved."]);
            new MachineConfigWriter(file.Path).Update(w =>
            {
                if (w["Actions"] is not JsonObject actions) return;
                foreach (var key in actions.Select(kv => kv.Key).Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList())
                    actions.Remove(key);
            });
            return Results.NoContent();
        });

        // Runs the action once with a test message. Admin only: it can run scripts and send email.
        api.MapPost("/config/actions/test", async (ActionConfig body, HttpContext http, ICallerIdentity callers,
            IOptionsMonitor<WallNutOptions> options, ActionDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!callers.Get(http).IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var action = FromConfig(body, string.IsNullOrEmpty(body.Id) ? null : options.CurrentValue.Actions.GetValueOrDefault(body.Id));
            if (ActionValidation.Validate(body.Id is { Length: > 0 } i ? i : "test", action) is { Count: > 0 } problems)
                return Results.Ok(new ActionTestResult(false, string.Join(" ", problems)));
            var result = await dispatcher.RunOnceAsync(action, ActionMessage.Test(), ct);
            return Results.Ok(new ActionTestResult(result.Ok, result.Message));
        });
    }

    /// <summary>An action as apps see it: every secret removed, with flags saying which are stored.</summary>
    public static ActionConfig ToView(string id, ActionOptions a) => new()
    {
        Id = id, Name = a.Name, Type = a.Type, Enabled = a.Enabled, Events = a.EffectiveEvents.ToList(),
        Ups = a.Ups.ToList(), DelaySeconds = a.DelaySeconds,
        Ntfy = a.Ntfy is { } n ? new NtfyConfig { Server = n.Server, Topic = n.Topic, TokenSet = !string.IsNullOrEmpty(n.Token) } : null,
        Webhook = a.Webhook is { } w ? new WebhookConfig
        {
            Url = w.Url, Method = w.Method, Preset = w.Preset, Body = w.Body, ContentType = w.ContentType,
            Headers = w.Headers.ToDictionary(h => h.Key, _ => ""), SecretSet = !string.IsNullOrEmpty(w.Secret),
        } : null,
        Script = a.Script is { } s ? new ScriptConfig
        {
            Path = s.Path, Arguments = s.Arguments, TimeoutSeconds = s.TimeoutSeconds,
            RunBeforeShutdown = s.RunBeforeShutdown, RunInTestMode = s.RunInTestMode,
        } : null,
        Email = a.Email is { } e ? new EmailConfig
        {
            Host = e.Host, Port = e.Port, Security = e.Security, Username = e.Username, From = e.From, To = e.To.ToList(),
            PasswordSet = !string.IsNullOrEmpty(e.Password),
        } : null,
    };

    /// <summary>
    /// Settings from the app, with stored secrets filling in what was left blank (null keeps a
    /// secret, "" clears it; an empty header value keeps that header's stored value).
    /// </summary>
    public static ActionOptions FromConfig(ActionConfig c, ActionOptions? stored)
    {
        static string? Keep(string? sent, string? old) => sent is null ? old : sent.Length == 0 ? null : sent;
        return new ActionOptions
        {
            Name = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim(),
            Type = c.Type,
            Enabled = c.Enabled,
            Events = c.Events.Distinct().ToList(),
            Ups = c.Ups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DelaySeconds = c.DelaySeconds,
            Ntfy = c.Type == ActionTypes.Ntfy && c.Ntfy is { } n ? new NtfyOptions
            {
                Server = n.Server.Trim(), Topic = n.Topic.Trim(), Token = Keep(n.Token, stored?.Ntfy?.Token),
            } : null,
            Webhook = c.Type == ActionTypes.Webhook && c.Webhook is { } w ? new WebhookOptions
            {
                Url = w.Url.Trim(), Method = w.Method.ToUpperInvariant(), Preset = w.Preset, Body = w.Body,
                ContentType = string.IsNullOrWhiteSpace(w.ContentType) ? "application/json" : w.ContentType.Trim(),
                Headers = w.Headers
                    .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                    .ToDictionary(h => h.Key.Trim(),
                        h => h.Value.Length == 0 && stored?.Webhook?.Headers.TryGetValue(h.Key.Trim(), out var old) == true ? old : h.Value,
                        StringComparer.OrdinalIgnoreCase),
                Secret = Keep(w.Secret, stored?.Webhook?.Secret),
            } : null,
            Script = c.Type == ActionTypes.Script && c.Script is { } s ? new ScriptOptions
            {
                Path = s.Path.Trim().Trim('"'), Arguments = string.IsNullOrWhiteSpace(s.Arguments) ? null : s.Arguments,
                TimeoutSeconds = s.TimeoutSeconds, RunBeforeShutdown = s.RunBeforeShutdown, RunInTestMode = s.RunInTestMode,
            } : null,
            Email = c.Type == ActionTypes.Email && c.Email is { } e ? new EmailOptions
            {
                Host = e.Host.Trim(), Port = e.Port, Security = e.Security,
                Username = string.IsNullOrWhiteSpace(e.Username) ? null : e.Username.Trim(),
                Password = Keep(e.Password, stored?.Email?.Password),
                From = e.From.Trim(), To = e.To.Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
            } : null,
        };
    }

    private static IResult Problems(IReadOnlyList<string> problems) =>
        Results.Json(new SettingsProblem(problems), Shared.WallNutJson.Options, statusCode: StatusCodes.Status400BadRequest);
}
