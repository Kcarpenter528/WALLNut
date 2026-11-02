using Microsoft.Extensions.Time.Testing;
using WallNut.Shared.Notifications;

namespace WallNut.Shared.Tests;

public class NotificationPreferencesTests
{
    private static ServiceEvent E(string kind, string endpoint = "nas") =>
        new(1, endpoint, endpoint, kind, DateTimeOffset.UtcNow, null, "msg", true);

    [Fact]
    public void Defaults_show_important_events_and_hide_routine_ones()
    {
        var p = new NotificationPreferences();
        Assert.True(p.Wants(E("OnBattery")));
        Assert.True(p.Wants(E("LowBattery")));
        Assert.True(p.Wants(E("CommunicationLost")));
        Assert.False(p.Wants(E("Connected")));
        Assert.False(p.Wants(E("Calibrating")));
    }

    [Fact]
    public void Group_switch_turns_off_its_kinds()
    {
        var p = new NotificationPreferences { Groups = { ["Connection"] = false } };
        Assert.False(p.Wants(E("CommunicationLost")));
        Assert.True(p.Wants(E("OnBattery")));
    }

    [Fact]
    public void Kind_switch_overrides_its_default()
    {
        var p = new NotificationPreferences { Kinds = { ["OnLine"] = false, ["Connected"] = true } };
        Assert.False(p.Wants(E("OnLine")));
        Assert.True(p.Wants(E("Connected")));
    }

    [Fact]
    public void Muted_ups_is_silent()
    {
        var p = new NotificationPreferences { MutedUps = { "office" } };
        Assert.False(p.Wants(E("OnBattery", "office")));
        Assert.True(p.Wants(E("OnBattery", "nas")));
    }

    [Theory]
    [InlineData("ShutdownInitiated")]
    [InlineData("ShutdownFailed")]
    [InlineData("ForcedShutdown")]
    [InlineData("Test")]
    public void Shutdown_warnings_cannot_be_silenced(string kind)
    {
        var p = new NotificationPreferences
        {
            Enabled = false,
            Groups = { ["Shutdown"] = false, ["Informational"] = false },
            Kinds = { [kind] = false },
            MutedUps = { "nas", "this-machine" },
        };
        Assert.True(p.Wants(E(kind)));
    }

    [Fact]
    public void Master_switch_silences_everything_else()
    {
        var p = new NotificationPreferences { Enabled = false };
        Assert.False(p.Wants(E("OnBattery")));
    }

    [Fact]
    public void Unknown_future_kinds_still_show()
    {
        Assert.True(new NotificationPreferences().Wants(E("SomethingNew")));
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wallnut-prefs-{Guid.NewGuid():N}", "notifications.json");
        try
        {
            new NotificationPreferences
            {
                Enabled = false, Groups = { ["Power"] = false }, Kinds = { ["OnLine"] = false },
                MutedUps = { "office" }, OnBatteryDelaySeconds = 12,
            }.Save(path);

            var p = NotificationPreferences.Load(path);
            Assert.False(p.Enabled);
            Assert.False(p.Groups["Power"]);
            Assert.False(p.Kinds["OnLine"]);
            Assert.Contains("OFFICE", p.MutedUps); // case-insensitive after loading too
            Assert.Equal(12, p.OnBatteryDelaySeconds);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Damaged_file_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wallnut-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.True(NotificationPreferences.Load(path).Enabled); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_gives_defaults() =>
        Assert.True(NotificationPreferences.Load(Path.Combine(Path.GetTempPath(), "nope", "x.json")).Enabled);
}

public class NotificationFilterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<ServiceEvent> _shown = [];
    private NotificationPreferences _prefs = new() { OnBatteryDelaySeconds = 5 };
    private long _seq;

    private NotificationFilter Make()
    {
        var f = new NotificationFilter(() => _prefs, _time);
        f.Show += e => { lock (_shown) _shown.Add(e); };
        return f;
    }

    private ServiceEvent E(string kind, string endpoint = "nas") =>
        new(++_seq, endpoint, endpoint, kind, _time.GetUtcNow(), null, kind, true);

    private string[] Shown() { lock (_shown) return _shown.Select(e => $"{e.EndpointId}:{e.Kind}").ToArray(); }

    [Fact]
    public void Ordinary_events_show_immediately()
    {
        using var f = Make();
        f.Handle(E("ReplaceBattery"));
        Assert.Equal(["nas:ReplaceBattery"], Shown());
    }

    [Fact]
    public void Power_failure_is_held_then_shown()
    {
        using var f = Make();
        f.Handle(E("OnBattery"));
        Assert.Empty(Shown());

        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(Shown());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["nas:OnBattery"], Shown());

        f.Handle(E("OnLine"));
        Assert.Equal(["nas:OnBattery", "nas:OnLine"], Shown());
    }

    [Fact]
    public void Power_blip_shows_nothing()
    {
        using var f = Make();
        f.Handle(E("OnBattery"));
        _time.Advance(TimeSpan.FromSeconds(2));
        f.Handle(E("OnLine"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Shown());
    }

    [Fact]
    public void Low_battery_during_the_hold_releases_the_power_failure_first()
    {
        using var f = Make();
        f.Handle(E("OnBattery"));
        f.Handle(E("LowBattery"));
        Assert.Equal(["nas:OnBattery", "nas:LowBattery"], Shown());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, Shown().Length); // not shown twice
    }

    [Fact]
    public void Each_ups_is_held_separately()
    {
        using var f = Make();
        f.Handle(E("OnBattery", "nas"));
        _time.Advance(TimeSpan.FromSeconds(3));
        f.Handle(E("OnBattery", "office"));
        f.Handle(E("OnLine", "nas"));           // nas was a blip
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(["office:OnBattery"], Shown());
    }

    [Fact]
    public void Zero_delay_announces_immediately()
    {
        _prefs = new NotificationPreferences { OnBatteryDelaySeconds = 0 };
        using var f = Make();
        f.Handle(E("OnBattery"));
        Assert.Equal(["nas:OnBattery"], Shown());
    }

    [Fact]
    public void Preferences_apply_after_the_hold()
    {
        using var f = Make();
        f.Handle(E("OnBattery", "office"));
        _prefs.MutedUps.Add("office"); // muted while it was held
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(Shown());
    }

    [Fact]
    public void Shutdown_from_this_machine_is_not_delayed()
    {
        using var f = Make();
        f.Handle(E("ShutdownInitiated", "this-machine"));
        Assert.Equal(["this-machine:ShutdownInitiated"], Shown());
    }
}
