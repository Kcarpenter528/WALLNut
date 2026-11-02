using System.Runtime.InteropServices;
using WallNut.Nut;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;

namespace WallNut.Service.Tests;

public class ShutdownPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    internal static UpsState Ups(string id, string? status, UpsRole role = UpsRole.PowersThisMachine,
        bool connected = true, double? charge = null, double? runtime = null, int powerValue = 1,
        TimeSpan? onBatteryFor = null, TimeSpan? disconnectedFor = null, TimeSpan? staleFor = null)
    {
        var vars = new Dictionary<string, string>();
        if (status is not null) vars["ups.status"] = status;
        if (charge is { } c) vars["battery.charge"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (runtime is { } r) vars["battery.runtime"] = r.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parsed = status is null ? (UpsStatus?)null : UpsStatus.Parse(status);
        return new UpsState
        {
            EndpointId = id, DisplayName = id, Host = "h", Port = 3493, Ups = "ups", Role = role, PowerValue = powerValue,
            Connected = connected,
            Status = parsed,
            Vars = vars,
            OnBatterySince = parsed?.IsOnBattery == true ? Now - (onBatteryFor ?? TimeSpan.FromSeconds(1)) : null,
            DisconnectedSince = connected ? null : Now - (disconnectedFor ?? TimeSpan.Zero),
            Stale = staleFor is not null,
            StaleSince = staleFor is { } s ? Now - s : null,
        };
    }

    private static ShutdownDecision Decide(ShutdownOptions? o, params UpsState[] states) =>
        ShutdownPolicy.Evaluate(states, o ?? new ShutdownOptions(), Now);

    // ---- Single UPS -------------------------------------------------------------------

    [Fact] public void On_line_is_fine() => Assert.False(Decide(null, Ups("nas", "OL")).ShouldShutDown);
    [Fact] public void On_battery_alone_is_not_enough() => Assert.False(Decide(null, Ups("nas", "OB DISCHRG", charge: 80)).ShouldShutDown);

    [Fact]
    public void On_battery_with_low_battery_shuts_down()
    {
        var d = Decide(null, Ups("nas", "OB DISCHRG LB"));
        Assert.True(d.ShouldShutDown);
        Assert.Contains("low battery", d.Summary);
    }

    [Fact] public void Low_battery_while_on_line_is_not_critical() => Assert.False(Decide(null, Ups("nas", "OL LB")).ShouldShutDown);

    [Fact]
    public void Forced_shutdown_wins_even_on_line_power()
    {
        Assert.True(Decide(null, Ups("nas", "OL FSD")).ShouldShutDown);
        Assert.False(Decide(new ShutdownOptions { RespectForcedShutdown = false }, Ups("nas", "OL FSD")).ShouldShutDown);
    }

    [Fact]
    public void Low_battery_rule_can_be_turned_off()
    {
        Assert.False(Decide(new ShutdownOptions { OnLowBattery = false }, Ups("nas", "OB LB")).ShouldShutDown);
    }

    [Fact]
    public void Charge_limit()
    {
        var o = new ShutdownOptions { BatteryChargeBelowPercent = 30 };
        Assert.False(Decide(o, Ups("nas", "OB", charge: 31)).ShouldShutDown);
        var d = Decide(o, Ups("nas", "OB", charge: 29));
        Assert.True(d.ShouldShutDown);
        Assert.Contains("29%", d.Summary);
    }

    [Fact]
    public void Runtime_limit()
    {
        var o = new ShutdownOptions { RuntimeBelowSeconds = 300 };
        Assert.False(Decide(o, Ups("nas", "OB", runtime: 301)).ShouldShutDown);
        Assert.True(Decide(o, Ups("nas", "OB", runtime: 299)).ShouldShutDown);
        Assert.False(Decide(o, Ups("nas", "OL", runtime: 10)).ShouldShutDown); // only counts on battery
    }

    [Fact]
    public void Time_on_battery_limit()
    {
        var o = new ShutdownOptions { OnBatteryForSeconds = 600 };
        Assert.False(Decide(o, Ups("nas", "OB", onBatteryFor: TimeSpan.FromSeconds(599))).ShouldShutDown);
        Assert.True(Decide(o, Ups("nas", "OB", onBatteryFor: TimeSpan.FromSeconds(600))).ShouldShutDown);
    }

    // ---- Fail-safe ---------------------------------------------------------------------

    [Fact]
    public void Lost_contact_on_line_power_is_not_critical()
    {
        Assert.False(Decide(null, Ups("nas", "OL", connected: false, disconnectedFor: TimeSpan.FromHours(1))).ShouldShutDown);
    }

    [Fact]
    public void Never_reached_is_assumed_fine()
    {
        Assert.False(Decide(null, Ups("nas", null, connected: false, disconnectedFor: TimeSpan.FromHours(1))).ShouldShutDown);
    }

    [Fact]
    public void Lost_contact_on_battery_shuts_down_after_the_grace_period()
    {
        Assert.False(Decide(null, Ups("nas", "OB", connected: false, disconnectedFor: TimeSpan.FromSeconds(14))).ShouldShutDown);
        var d = Decide(null, Ups("nas", "OB", connected: false, disconnectedFor: TimeSpan.FromSeconds(15)));
        Assert.True(d.ShouldShutDown);
        Assert.Contains("last seen on battery", d.Summary);
    }

    [Fact]
    public void Stale_data_on_battery_counts_like_lost_contact()
    {
        Assert.True(Decide(null, Ups("nas", "OB", staleFor: TimeSpan.FromSeconds(20))).ShouldShutDown);
        Assert.False(Decide(null, Ups("nas", "OL", staleFor: TimeSpan.FromSeconds(20))).ShouldShutDown);
    }

    [Fact]
    public void Last_known_critical_state_still_counts_after_contact_is_lost()
    {
        // Low battery was reported, then the network died a second later: don't wait 15 s.
        Assert.True(Decide(null, Ups("nas", "OB LB", connected: false, disconnectedFor: TimeSpan.FromSeconds(1))).ShouldShutDown);
    }

    // ---- Several UPSes -----------------------------------------------------------------

    [Fact]
    public void Monitor_only_ups_never_triggers_a_shutdown()
    {
        var d = Decide(null, Ups("office", "OB LB FSD", role: UpsRole.Monitor));
        Assert.False(d.ShouldShutDown);
        Assert.Empty(d.Supplies);
    }

    [Fact]
    public void Monitor_only_ups_does_not_count_as_a_supply()
    {
        var d = Decide(null, Ups("nas", "OB LB"), Ups("office", "OL", role: UpsRole.Monitor));
        Assert.True(d.ShouldShutDown);
    }

    [Fact]
    public void Dual_supply_server_survives_one_ups_failing()
    {
        var o = new ShutdownOptions { MinimumSupplies = 1 };
        Assert.False(Decide(o, Ups("a", "OB LB"), Ups("b", "OL")).ShouldShutDown);
        Assert.True(Decide(o, Ups("a", "OB LB"), Ups("b", "OB LB")).ShouldShutDown);
    }

    [Fact]
    public void Server_needing_both_supplies_goes_when_either_fails()
    {
        var o = new ShutdownOptions { MinimumSupplies = 2 };
        var d = Decide(o, Ups("a", "OB LB"), Ups("b", "OL"));
        Assert.True(d.ShouldShutDown);
        Assert.Equal(1, d.HealthySupplies);
        Assert.Contains("a:", d.Summary);
    }

    [Fact]
    public void Power_value_counts_supplies()
    {
        var o = new ShutdownOptions { MinimumSupplies = 2 };
        Assert.False(Decide(o, Ups("big", "OL", powerValue: 2), Ups("small", "OB LB")).ShouldShutDown);
    }

    [Fact]
    public void Shutdown_disabled_never_decides_to_shut_down()
    {
        Assert.False(Decide(new ShutdownOptions { Enabled = false }, Ups("nas", "OB LB")).ShouldShutDown);
    }

    [Fact]
    public void Config_catches_impossible_minimum_supplies()
    {
        var o = new WallNutOptions
        {
            Endpoints = { ["nas"] = new EndpointOptions { Host = "x", Role = UpsRole.PowersThisMachine, Username = "u", Password = "p" } },
            Shutdown = new ShutdownOptions { MinimumSupplies = 2 },
        };
        Assert.Contains(o.Validate(), p => p.Contains("MinimumSupplies"));
    }
}

public class ShutdownCommandTests
{
    [Fact]
    public void Windows_shutdown_records_a_power_failure_reason()
    {
        var (file, args) = ShutdownCommand.For(OSPlatform.Windows, ShutdownAction.Shutdown, "on battery \"low\"");
        Assert.Equal("shutdown.exe", file);
        Assert.Equal(["/s", "/f", "/t", "0", "/d", "6:12", "/c", "WALLNut: on battery 'low'"], args);
    }

    private static string Line(OSPlatform os, ShutdownAction a)
    {
        var (file, args) = ShutdownCommand.For(os, a, "x");
        return file + " " + string.Join(' ', args);
    }

    [Fact]
    public void Windows_hibernate() => Assert.Equal("shutdown.exe /h /f", Line(OSPlatform.Windows, ShutdownAction.Hibernate));

    [Fact]
    public void Linux_uses_systemd()
    {
        Assert.Equal("systemctl poweroff", Line(OSPlatform.Linux, ShutdownAction.Shutdown));
        Assert.Equal("systemctl hibernate", Line(OSPlatform.Linux, ShutdownAction.Hibernate));
    }

    [Fact]
    public void Mac_uses_shutdown_and_pmset()
    {
        Assert.Equal("/sbin/shutdown -h now WALLNut: x", Line(OSPlatform.OSX, ShutdownAction.Shutdown));
        Assert.Equal("/usr/bin/pmset sleepnow", Line(OSPlatform.OSX, ShutdownAction.Hibernate));
    }

    [Fact]
    public void Very_long_reasons_are_trimmed_for_windows()
    {
        var (_, args) = ShutdownCommand.For(OSPlatform.Windows, ShutdownAction.Shutdown, new string('x', 2000));
        Assert.True(args[^1].Length <= 500);
    }
}
