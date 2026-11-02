using System.Globalization;

namespace WallNut.Simulator.Tests;

/// <summary>The simulated physics: outages, draining, low battery, running dry, recharging, alarms and timers.</summary>
public class BatteryModelTests
{
    private static SimulatedUps Make(string profile = "full-featured") =>
        new("ups", UpsProfile.Load(profile), new DateTime(2026, 11, 2, 9, 0, 0)) { Jitter = false };

    private static double Num(SimulatedUps u, string name) =>
        double.Parse(u.Get(name)!, CultureInfo.InvariantCulture);

    private static string[] Flags(SimulatedUps u) => u.Get("ups.status")!.Split(' ');

    [Fact]
    public void Starts_on_line_power_with_a_full_battery()
    {
        var u = Make();
        Assert.Equal("OL", u.Get("ups.status"));
        Assert.Equal("100", u.Get("battery.charge"));
        Assert.True(Num(u, "input.voltage") > 100);
        Assert.Equal("resting", u.Get("battery.charger.status"));
    }

    [Fact]
    public void Outage_switches_to_battery_and_drains_it()
    {
        var u = Make();
        u.PowerFail();

        Assert.Equal(["OB", "DISCHRG"], Flags(u));
        Assert.Equal("0.0", u.Get("input.voltage"));
        Assert.Equal("discharging", u.Get("battery.charger.status"));
        Assert.Equal("input voltage out of range", u.Get("input.transfer.reason"));

        var runtimeBefore = Num(u, "battery.runtime");
        u.Advance(TimeSpan.FromMinutes(5));
        Assert.True(u.Charge < 100);
        Assert.True(Num(u, "battery.runtime") < runtimeBefore);
    }

    [Fact]
    public void Heavier_load_drains_faster()
    {
        var light = Make(); light.SetLoad(10); light.PowerFail();
        var heavy = Make(); heavy.SetLoad(90); heavy.PowerFail();

        light.Advance(TimeSpan.FromMinutes(2));
        heavy.Advance(TimeSpan.FromMinutes(2));

        Assert.True(heavy.Charge < light.Charge);
    }

    [Fact]
    public void Low_battery_flag_appears_at_the_ups_threshold()
    {
        var u = Make();
        u.PowerFail();
        u.SetCharge(25);
        Assert.DoesNotContain("LB", Flags(u));

        u.SetCharge(19); // battery.charge.low is 20
        Assert.Contains("LB", Flags(u));
    }

    [Fact]
    public void Low_battery_also_triggers_on_low_runtime()
    {
        var u = Make();
        u.Set("battery.runtime.low", "1800"); // needs admin over the network; direct here
        u.PowerFail();
        u.SetLoad(80);
        Assert.Contains("LB", Flags(u));
    }

    [Fact]
    public void Running_dry_cuts_the_load_then_power_return_restarts_it()
    {
        var u = Make();
        u.PowerFail();
        u.SetCharge(1);
        u.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, u.Charge);
        Assert.Contains("OFF", Flags(u));
        Assert.Equal("off", u.Get("outlet.status"));

        u.PowerRestore();
        u.Advance(TimeSpan.FromSeconds(Num(u, "ups.delay.start") + 1));
        Assert.DoesNotContain("OFF", Flags(u));
        Assert.Contains("CHRG", Flags(u));
    }

    [Fact]
    public void Recharges_after_power_returns()
    {
        var u = Make();
        u.SetCharge(50);
        Assert.Contains("CHRG", Flags(u));
        Assert.Equal("charging", u.Get("battery.charger.status"));

        u.Advance(TimeSpan.FromSeconds(5400 + 60)); // 50% of a 3 h full recharge, plus a minute
        Assert.Equal(100, u.Charge);
        Assert.Equal("OL", u.Get("ups.status"));
        Assert.Equal("floating", u.Get("battery.charger.status"));

        u.Advance(TimeSpan.FromMinutes(10)); // floats for 10 minutes after filling, then rests
        Assert.Equal("resting", u.Get("battery.charger.status"));
    }

    [Fact]
    public void Mains_voltage_outside_transfer_limits_goes_to_battery()
    {
        var u = Make();
        u.SetInputVoltage(85); // input.transfer.low is 92
        Assert.Contains("OB", Flags(u));
        u.SetInputVoltage(121);
        Assert.Contains("OL", Flags(u));
    }

    [Fact]
    public void Alarms_replace_battery_and_overload()
    {
        var u = Make();
        u.SetReplaceBattery(true);
        u.SetLoad(110);

        Assert.Contains("RB", Flags(u));
        Assert.Contains("OVER", Flags(u));
        Assert.Contains("ALARM", Flags(u));
        Assert.Contains("Replace battery!", u.Get("ups.alarm"));
        Assert.Equal("1", u.Get("battery.packs.bad"));
    }

    [Fact]
    public void Forced_shutdown_flag_latches()
    {
        var u = Make();
        u.SetForcedShutdown();
        u.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("FSD", Flags(u));
    }

    [Fact]
    public void Shutdown_return_turns_off_after_delay_and_comes_back_with_power()
    {
        var u = Make();
        u.PowerFail();
        Assert.Equal(SimResult.Ok, u.RunCommand("shutdown.return"));
        Assert.Equal("20", u.Get("ups.timer.shutdown"));

        u.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("10", u.Get("ups.timer.shutdown"));
        Assert.DoesNotContain("OFF", Flags(u));

        u.Advance(TimeSpan.FromSeconds(11));
        Assert.Contains("OFF", Flags(u));
        Assert.Equal("-1", u.Get("ups.timer.shutdown"));

        u.PowerRestore();
        Assert.Equal("30", u.Get("ups.timer.start"));
        u.Advance(TimeSpan.FromSeconds(31));
        Assert.DoesNotContain("OFF", Flags(u));
    }

    [Fact]
    public void Shutdown_stop_cancels_a_pending_shutdown()
    {
        var u = Make();
        u.RunCommand("shutdown.stayoff");
        u.RunCommand("shutdown.stop");
        u.Advance(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain("OFF", Flags(u));
    }

    [Fact]
    public void Battery_test_reports_progress_then_result()
    {
        var u = Make();
        u.RunCommand("test.battery.start.quick");
        Assert.Equal("In progress", u.Get("ups.test.result"));
        u.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("Done and passed", u.Get("ups.test.result"));
        Assert.Equal("2026-11-02", u.Get("ups.test.date"));
    }

    [Fact]
    public void Calibration_runs_on_battery_until_a_quarter_charge()
    {
        var u = Make();
        u.RunCommand("calibrate.start");
        Assert.Contains("CAL", Flags(u));
        Assert.Contains("DISCHRG", Flags(u));

        // ~30 min to drain to 25% at this load, then it starts recharging.
        u.Advance(TimeSpan.FromHours(1));
        Assert.DoesNotContain("CAL", Flags(u));
        Assert.InRange(u.Charge, 25, 60);
    }

    [Fact]
    public void Failure_test_simulates_an_outage()
    {
        var u = Make();
        u.RunCommand("test.failure.start");
        Assert.Contains("OB", Flags(u));
        Assert.Equal("self test", u.Get("input.transfer.reason"));
        u.RunCommand("test.failure.stop");
        Assert.Contains("OL", Flags(u));
    }

    [Fact]
    public void Beeper_and_outlet_commands_change_their_variables()
    {
        var u = Make();
        u.RunCommand("beeper.mute");
        Assert.Equal("muted", u.Get("ups.beeper.status"));

        u.RunCommand("outlet.2.load.off");
        Assert.Equal("off", u.Get("outlet.2.status"));
        Assert.Equal("on", u.Get("outlet.1.status"));
        Assert.Equal("0.0", u.Get("outlet.2.current"));
    }

    [Fact]
    public void Writable_settings_are_validated()
    {
        var u = Make();
        Assert.Equal(SimResult.Ok, u.Set("input.sensitivity", "high"));
        Assert.Equal(SimResult.InvalidValue, u.Set("input.sensitivity", "extreme"));
        Assert.Equal(SimResult.InvalidValue, u.Set("battery.charge.low", "95")); // range 5-90
        Assert.Equal(SimResult.Readonly, u.Set("ups.status", "OB"));
        Assert.Equal(SimResult.UnknownVariable, u.Set("ups.nope", "1"));
        Assert.Equal(SimResult.UnknownCommand, u.RunCommand("make.coffee"));
        Assert.Equal(SimResult.InvalidArgument, u.RunCommand("load.off.delay", "soon"));
    }

    [Fact]
    public void Sparse_profile_never_gains_variables()
    {
        var u = Make("back-ups");
        var before = u.Snapshot().Select(kv => kv.Key).ToList();
        u.PowerFail();
        u.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(before, u.Snapshot().Select(kv => kv.Key));
        Assert.Contains("OB", Flags(u));
    }

    [Fact]
    public void Three_phase_readings_follow_the_load_and_outages()
    {
        var u = Make("three-phase");
        var l1 = Num(u, "input.L1-N.voltage");
        Assert.InRange(l1, 225, 235);                                      // 400 V / sqrt(3)
        Assert.InRange(Num(u, "input.L1-L2.voltage"), 395, 405);
        Assert.Equal(Num(u, "output.L1.power"), Num(u, "output.L2.power"), 1);

        var before = Num(u, "output.L1.current");
        u.SetLoad(76);
        Assert.True(Num(u, "output.L1.current") > before * 1.9);

        u.PowerFail();
        Assert.Equal("0.0", u.Get("input.L2-N.voltage"));
        Assert.Equal("0", u.Get("input.L3.realpower"));
        Assert.True(Num(u, "output.L1-N.voltage") > 225);                  // still powering the load
    }
}
