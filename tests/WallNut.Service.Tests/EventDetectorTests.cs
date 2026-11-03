using WallNut.Nut;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;

namespace WallNut.Service.Tests;

public class EventDetectorTests
{
    private static IReadOnlyList<UpsEventKind> D(string? before, string after) =>
        EventDetector.Detect(before is null ? null : UpsStatus.Parse(before), UpsStatus.Parse(after));

    [Fact] public void No_change_no_events() => Assert.Empty(D("OL", "OL"));
    [Fact] public void Charging_noise_is_not_an_event() => Assert.Empty(D("OL", "OL CHRG"));
    [Fact] public void Power_failure() => Assert.Equal([UpsEventKind.OnBattery], D("OL", "OB DISCHRG"));
    [Fact] public void Power_return() => Assert.Equal([UpsEventKind.OnLine], D("OB DISCHRG", "OL CHRG"));
    [Fact] public void Low_battery() => Assert.Equal([UpsEventKind.LowBattery], D("OB DISCHRG", "OB DISCHRG LB"));
    [Fact] public void Outage_with_low_battery_at_once() => Assert.Equal([UpsEventKind.OnBattery, UpsEventKind.LowBattery], D("OL", "OB LB"));
    [Fact] public void Forced_shutdown() => Assert.Equal([UpsEventKind.ForcedShutdown], D("OB LB", "OB LB FSD"));
    [Fact] public void Replace_battery_set_and_cleared()
    {
        Assert.Equal([UpsEventKind.ReplaceBattery], D("OL", "OL RB"));
        Assert.Equal([UpsEventKind.ReplaceBatteryCleared], D("OL RB", "OL"));
    }
    [Fact] public void Overload_set_and_cleared()
    {
        Assert.Equal(new[] { UpsEventKind.Overload, UpsEventKind.Alarm }, D("OL", "ALARM OL OVER"));
        Assert.Equal(new[] { UpsEventKind.OverloadCleared, UpsEventKind.AlarmCleared }, D("ALARM OL OVER", "OL"));
    }
    [Fact] public void Output_off_and_back()
    {
        Assert.Equal([UpsEventKind.Off], D("OB DISCHRG LB", "OFF OB LB"));
        Assert.Equal([UpsEventKind.NotOff], D("OFF OL", "OL CHRG"));
    }

    [Fact] public void First_reading_on_line_is_quiet() => Assert.Empty(D(null, "OL CHRG"));
    [Fact] public void First_reading_mid_outage_reports_it() =>
        Assert.Equal([UpsEventKind.OnBattery, UpsEventKind.LowBattery], D(null, "OB DISCHRG LB"));
}

public class ReconnectBackoffTests
{
    [Fact]
    public void Doubles_up_to_the_max_and_resets()
    {
        var b = new ReconnectBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), new Random(1));
        var waits = Enumerable.Range(0, 6).Select(_ => b.Next().TotalSeconds).ToList();

        Assert.InRange(waits[0], 1.8, 2.2);
        Assert.InRange(waits[1], 3.6, 4.4);
        Assert.InRange(waits[2], 7.2, 8.8);
        Assert.All(waits.Skip(3), w => Assert.InRange(w, 9, 10));
        Assert.Equal(6, b.Failures);

        b.Reset();
        Assert.Equal(0, b.Failures);
        Assert.InRange(b.Next().TotalSeconds, 1.8, 2.2);
    }
}

public class OptionsTests
{
    [Fact]
    public void Valid_config_has_no_problems()
    {
        var o = new WallNutOptions { Endpoints = { ["nas"] = new EndpointOptions { Host = "nas.local" } } };
        Assert.Empty(o.Validate());
    }

    [Fact]
    public void Problems_are_explained()
    {
        var o = new WallNutOptions
        {
            Endpoints =
            {
                ["a"] = new EndpointOptions { Host = "" },
                ["b"] = new EndpointOptions { Host = "x", Role = UpsRole.PowersThisMachine },
                ["c"] = new EndpointOptions { Host = "x", Username = "u" },
            },
        };
        var p = o.Validate();
        Assert.Contains(p, s => s.Contains("'a'") && s.Contains("Host"));
        Assert.Contains(p, s => s.Contains("'b'") && s.Contains("monuser"));
        Assert.Contains(p, s => s.Contains("'c'") && s.Contains("both"));
    }

    [Fact]
    public void Nothing_configured_is_allowed_but_noted()
    {
        var o = new WallNutOptions();
        Assert.Empty(o.Validate()); // a fresh install adds its UPS in the app
        Assert.True(o.HasNoUps);
    }

    [Fact]
    public void Disabled_endpoints_are_ignored()
    {
        var o = new WallNutOptions
        {
            Endpoints =
            {
                ["ok"] = new EndpointOptions { Host = "x" },
                ["off"] = new EndpointOptions { Host = "", Enabled = false },
            },
        };
        Assert.Empty(o.Validate());
    }
}
