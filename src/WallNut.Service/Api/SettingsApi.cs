using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using WallNut.Nut;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Shared.Settings;

namespace WallNut.Service.Api;

/// <summary>
/// Settings and live-detail endpoints on the local API. Anyone logged in may read; only
/// administrators may change settings (see <see cref="CallerIdentity"/>).
/// </summary>
public static class SettingsApi
{
    public static void MapSettingsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (HttpContext http, ICallerIdentity callers) =>
        {
            var (user, admin) = callers.Get(http);
            return new CallerInfo(user, admin);
        });

        api.MapGet("/ups/{id}", (string id, UpsStateStore store) =>
            store.Get(id) is { } s
                ? Results.Ok(new UpsDetail(LocalApi.ToSummary(s), s.ConnectedSince, s.Vars))
                : Results.NotFound());

        api.MapGet("/config", (HttpContext http, ICallerIdentity callers, IOptionsMonitor<WallNutOptions> options, MachineConfigStatus file) =>
        {
            var (_, admin) = callers.Get(http);
            var reason = file.Path is null ? "This service was started without a settings file."
                : !admin ? "Only an administrator can change these settings."
                : null;
            var o = options.CurrentValue;
            return new SettingsView(file.Path, reason is null, reason,
                o.Endpoints.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => ToView(e.Key, e.Value)).ToList(),
                ToView(o.Shutdown),
                o.Actions.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(a => ActionsApi.ToView(a.Key, a.Value)).ToList());
        });

        api.MapPut("/config/endpoints/{id}", (string id, EndpointConfig body, HttpContext http, ICallerIdentity callers,
            IOptionsMonitor<WallNutOptions> options, MachineConfigStatus file) =>
        {
            if (Refuse(http, callers, file) is { } refused) return refused;
            var problems = ValidateId(id);
            if (problems.Count > 0) return Problems(problems);

            var writer = new MachineConfigWriter(file.Path!);
            var password = body.Password ?? writer.StoredPassword(id)
                           ?? options.CurrentValue.Endpoints.GetValueOrDefault(id)?.Password;
            var endpoint = ToOptions(body, string.IsNullOrEmpty(body.Username) ? null : password);

            // Check the whole configuration as it would be after this change.
            var candidate = Clone(options.CurrentValue);
            candidate.Endpoints[id] = endpoint;
            if (candidate.Validate() is { Count: > 0 } invalid) return Problems(invalid);

            writer.Update(w =>
            {
                var endpoints = MachineConfigWriter.Endpoints(w);
                foreach (var key in endpoints.Select(kv => kv.Key).Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList())
                    endpoints.Remove(key);
                endpoints[id] = ToJson(endpoint);
            });
            return Results.NoContent();
        });

        api.MapDelete("/config/endpoints/{id}", (string id, HttpContext http, ICallerIdentity callers, IOptionsMonitor<WallNutOptions> options, MachineConfigStatus file) =>
        {
            if (Refuse(http, callers, file) is { } refused) return refused;
            var writer = new MachineConfigWriter(file.Path!);
            writer.Update(w =>
            {
                var endpoints = MachineConfigWriter.Endpoints(w);
                foreach (var key in endpoints.Select(kv => kv.Key).Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList())
                    endpoints.Remove(key);
            });
            return Results.NoContent();
        });

        api.MapPut("/config/shutdown", (ShutdownSettings body, HttpContext http, ICallerIdentity callers, IOptionsMonitor<WallNutOptions> options, MachineConfigStatus file) =>
        {
            if (Refuse(http, callers, file) is { } refused) return refused;
            if (!Enum.TryParse<ShutdownAction>(body.Action, out var action)) return Problems([$"Unknown shutdown action '{body.Action}'."]);

            var candidate = Clone(options.CurrentValue);
            candidate.Shutdown = new ShutdownOptions
            {
                Enabled = body.Enabled, DryRun = body.DryRun, Action = action, MinimumSupplies = body.MinimumSupplies,
                RespectForcedShutdown = body.RespectForcedShutdown, OnLowBattery = body.OnLowBattery,
                BatteryChargeBelowPercent = body.BatteryChargeBelowPercent, RuntimeBelowSeconds = body.RuntimeBelowSeconds,
                OnBatteryForSeconds = body.OnBatteryForSeconds, LostWhileOnBatterySeconds = body.LostWhileOnBatterySeconds,
                FinalDelaySeconds = body.FinalDelaySeconds,
            };
            if (candidate.Validate() is { Count: > 0 } invalid) return Problems(invalid);

            new MachineConfigWriter(file.Path!).Update(w => w["Shutdown"] = new JsonObject
            {
                ["Enabled"] = body.Enabled,
                ["DryRun"] = body.DryRun,
                ["Action"] = action.ToString(),
                ["MinimumSupplies"] = body.MinimumSupplies,
                ["RespectForcedShutdown"] = body.RespectForcedShutdown,
                ["OnLowBattery"] = body.OnLowBattery,
                ["BatteryChargeBelowPercent"] = body.BatteryChargeBelowPercent,
                ["RuntimeBelowSeconds"] = body.RuntimeBelowSeconds,
                ["OnBatteryForSeconds"] = body.OnBatteryForSeconds,
                ["LostWhileOnBatterySeconds"] = body.LostWhileOnBatterySeconds,
                ["FinalDelaySeconds"] = body.FinalDelaySeconds,
            });
            return Results.NoContent();
        });

        // Trying a connection is harmless (it only reads), so anyone may do it.
        api.MapPost("/config/test-endpoint", async (EndpointConfig body, IOptionsMonitor<WallNutOptions> options,
            MachineConfigStatus file, CancellationToken ct) =>
        {
            var password = body.Password;
            if (password is null && !string.IsNullOrEmpty(body.Id) && !string.IsNullOrEmpty(body.Username))
                password = (file.Path is { } p ? new MachineConfigWriter(p).StoredPassword(body.Id) : null)
                           ?? options.CurrentValue.Endpoints.GetValueOrDefault(body.Id)?.Password;
            return await TestAsync(body, password, ct);
        });
    }

    // ---- Testing a connection ------------------------------------------------------------

    private static async Task<EndpointTestResult> TestAsync(EndpointConfig e, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(e.Host)) return Fail("Enter a host name or IP address.");
        var available = new List<string>();
        try
        {
            await using var c = await NutClient.ConnectAsync(e.Host.Trim(), e.Port, TimeSpan.FromSeconds(Math.Clamp(e.TimeoutSeconds, 2, 30)), ct);
            if (!string.IsNullOrEmpty(e.Username)) await c.AuthenticateAsync(e.Username, password ?? "", ct);
            try { available.AddRange((await c.ListUpsAsync(ct)).Select(u => u.Name)); } catch (NutException) { }

            IReadOnlyDictionary<string, string> vars;
            try { vars = await c.ListVarsAsync(e.Ups, ct); }
            catch (NutException n) when (n.Code == NutErrorCode.UnknownUps)
            {
                return new EndpointTestResult(false,
                    available.Count > 0
                        ? $"Connected, but there's no UPS named \"{e.Ups}\". This server has: {string.Join(", ", available)}."
                        : $"Connected, but there's no UPS named \"{e.Ups}\".",
                    null, null, null, available, false);
            }

            var login = false;
            if (e.Role == "PowersThisMachine" && !string.IsNullOrEmpty(e.Username))
            {
                try { await c.LoginAsync(e.Ups, ct); login = true; }
                catch (NutException n)
                {
                    return new EndpointTestResult(false,
                        $"Reads work, but logging in as \"{e.Username}\" was refused ({n.RawCode}). A UPS that powers this computer needs a NUT account that may log in (Synology: monuser / secret).",
                        Model(vars), vars.GetValueOrDefault("ups.status"), Charge(vars), available, false);
                }
            }
            await c.LogoutAsync(ct);

            var status = UpsStatus.Parse(vars.GetValueOrDefault("ups.status"));
            return new EndpointTestResult(true,
                $"Connected: {Model(vars) ?? "UPS"}, {(status.IsOnBattery ? "on battery" : "on line power")}" +
                (Charge(vars) is { } ch ? $", battery {ch:0}%" : "") + (login ? ". Login works." : "."),
                Model(vars), status.Raw, Charge(vars), available, login);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new EndpointTestResult(false, "Couldn't connect: " + UpsMonitor.Explain(ex), null, null, null, available, false);
        }

        static EndpointTestResult Fail(string why) => new(false, why, null, null, null, [], false);
        static string? Model(IReadOnlyDictionary<string, string> v) =>
            v.GetValueOrDefault("device.model") ?? v.GetValueOrDefault("ups.model");
        static double? Charge(IReadOnlyDictionary<string, string> v) =>
            double.TryParse(v.GetValueOrDefault("battery.charge"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static IResult? Refuse(HttpContext http, ICallerIdentity callers, MachineConfigStatus file)
    {
        if (!callers.Get(http).IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (file.Path is null) return Problems(["This service was started without a settings file, so changes can't be saved."]);
        return null;
    }

    private static IResult Problems(IReadOnlyList<string> problems) =>
        Results.Json(new SettingsProblem(problems), Shared.WallNutJson.Options, statusCode: StatusCodes.Status400BadRequest);

    private static List<string> ValidateId(string id) =>
        string.IsNullOrWhiteSpace(id) || id.Length > 40 || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? ["The id must be 1-40 letters, digits, '-' or '_' (for example \"nas\" or \"office-ups\")."]
            : [];

    private static EndpointConfig ToView(string id, EndpointOptions e) => new()
    {
        Id = id, DisplayName = e.DisplayName, Host = e.Host, Port = e.Port, Ups = e.Ups, Username = e.Username,
        PasswordSet = !string.IsNullOrEmpty(e.Password), Role = e.Role.ToString(), PowerValue = e.PowerValue,
        PollIntervalSeconds = e.PollIntervalSeconds, TimeoutSeconds = e.TimeoutSeconds, Enabled = e.Enabled,
    };

    private static ShutdownSettings ToView(ShutdownOptions s) => new()
    {
        Enabled = s.Enabled, DryRun = s.DryRun, Action = s.Action.ToString(), MinimumSupplies = s.MinimumSupplies,
        RespectForcedShutdown = s.RespectForcedShutdown, OnLowBattery = s.OnLowBattery,
        BatteryChargeBelowPercent = s.BatteryChargeBelowPercent, RuntimeBelowSeconds = s.RuntimeBelowSeconds,
        OnBatteryForSeconds = s.OnBatteryForSeconds, LostWhileOnBatterySeconds = s.LostWhileOnBatterySeconds,
        FinalDelaySeconds = s.FinalDelaySeconds,
    };

    private static EndpointOptions ToOptions(EndpointConfig e, string? password) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(e.DisplayName) ? null : e.DisplayName.Trim(),
        Host = e.Host.Trim(), Port = e.Port, Ups = e.Ups.Trim(),
        Username = string.IsNullOrWhiteSpace(e.Username) ? null : e.Username.Trim(),
        Password = string.IsNullOrWhiteSpace(e.Username) ? null : password,
        Role = Enum.TryParse<UpsRole>(e.Role, out var role) ? role : UpsRole.Monitor,
        PowerValue = e.PowerValue, PollIntervalSeconds = e.PollIntervalSeconds, TimeoutSeconds = e.TimeoutSeconds,
        Enabled = e.Enabled,
    };

    private static JsonObject ToJson(EndpointOptions e)
    {
        var o = new JsonObject
        {
            ["Host"] = e.Host, ["Port"] = e.Port, ["Ups"] = e.Ups, ["Role"] = e.Role.ToString(),
            ["PowerValue"] = e.PowerValue, ["PollIntervalSeconds"] = e.PollIntervalSeconds,
            ["TimeoutSeconds"] = e.TimeoutSeconds, ["Enabled"] = e.Enabled,
        };
        if (e.DisplayName is not null) o["DisplayName"] = e.DisplayName;
        if (e.Username is not null) { o["Username"] = e.Username; o["Password"] = e.Password ?? ""; }
        return o;
    }

    private static WallNutOptions Clone(WallNutOptions o) => new()
    {
        Endpoints = new Dictionary<string, EndpointOptions>(o.Endpoints, StringComparer.OrdinalIgnoreCase),
        Reconnect = o.Reconnect,
        Shutdown = o.Shutdown,
        History = o.History,
        Actions = o.Actions,
    };
}
