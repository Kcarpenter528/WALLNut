using Microsoft.Extensions.Options;
using WallNut.Nut;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;

namespace WallNut.Service;

/// <summary>
/// "WallNut.Service --check": validates the settings and tries each UPS once, then exits.
/// Used by the installers so problems show up at install time, not during a power cut.
/// Exit code 0 = everything reachable, 1 = settings problem, 2 = a UPS couldn't be reached.
/// </summary>
public static class ConfigCheck
{
    public static async Task<int> RunAsync(WebApplication app, TextWriter output)
    {
        var o = app.Services.GetRequiredService<IOptions<WallNutOptions>>().Value;
        var file = app.Services.GetRequiredService<MachineConfigStatus>();
        output.WriteLine($"WALLNut settings check (machine settings: {file.Path ?? "none"}{(file.Path is null ? "" : file.Loaded ? ", loaded" : ", not found")})");
        if (file.Problem is { } unreadable)
        {
            output.WriteLine($"  PROBLEM: {unreadable}");
            return 1;
        }

        var problems = o.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) output.WriteLine($"  PROBLEM: {p}");
            return 1;
        }
        if (o.HasNoUps) output.WriteLine($"  NOTE: {WallNutOptions.NoUpsNote}");

        var failed = 0;
        foreach (var (id, e) in o.Endpoints.Where(e => e.Value.Enabled))
        {
            output.Write($"  {id}: {e.Ups}@{e.Host}:{e.Port} ({e.Role}) ... ");
            try
            {
                await using var c = await NutClient.ConnectAsync(e.Host, e.Port, TimeSpan.FromSeconds(e.TimeoutSeconds));
                if (e.Username is { } user) await c.AuthenticateAsync(user, e.Password!);
                var vars = await c.ListVarsAsync(e.Ups);
                var status = UpsStatus.Parse(vars.GetValueOrDefault("ups.status"));
                var model = vars.GetValueOrDefault("device.model") ?? vars.GetValueOrDefault("ups.model") ?? "unknown model";
                output.WriteLine($"OK - {model}, status {status}, battery {vars.GetValueOrDefault("battery.charge", "?")}%");
                if (e.Role == UpsRole.PowersThisMachine && e.Username is not null)
                {
                    await c.LoginAsync(e.Ups); // proves the account may log in as a secondary
                    output.WriteLine("      login as a secondary (upsmon) works");
                }
                await c.LogoutAsync();
            }
            catch (Exception ex)
            {
                failed++;
                output.WriteLine("FAILED - " + UpsMonitor.Explain(ex));
            }
        }

        var s = o.Shutdown;
        var powering = o.Endpoints.Where(x => x.Value.Enabled && x.Value.Role == UpsRole.PowersThisMachine).Select(x => x.Key).ToList();
        output.WriteLine(!s.Enabled ? "  Shutdown: OFF"
            : powering.Count == 0 ? "  Shutdown: no UPS has Role PowersThisMachine, so this machine will never be shut down"
            : $"  Shutdown: {(s.DryRun ? "TEST MODE (logs only)" : "ARMED")}, {s.Action} when fewer than {s.MinimumSupplies} of [{string.Join(", ", powering)}] are healthy");
        return failed == 0 ? 0 : 2;
    }
}
