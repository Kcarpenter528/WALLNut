using Microsoft.Extensions.Time.Testing;
using WallNut.Shared.Actions;

namespace WallNut.Shared.Tests;

public class EventHoldTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _out = [];
    private long _seq;

    private EventHold Make(double seconds)
    {
        var h = new EventHold(TimeSpan.FromSeconds(seconds), _time);
        h.Release += e => { lock (_out) _out.Add($"{e.EndpointId}:{e.Kind}"); };
        return h;
    }

    private ServiceEvent E(string kind, string ups = "nas") => new(++_seq, ups, ups, kind, _time.GetUtcNow(), null, kind, true);

    [Fact]
    public void No_delay_passes_everything_straight_through()
    {
        using var h = Make(0);
        h.Handle(E("OnBattery"));
        h.Handle(E("OnLine"));
        Assert.Equal(["nas:OnBattery", "nas:OnLine"], _out);
    }

    [Fact]
    public void Problem_that_clears_in_time_says_nothing()
    {
        using var h = Make(60);
        h.Handle(E("OnBattery"));
        _time.Advance(TimeSpan.FromSeconds(30));
        h.Handle(E("OnLine"));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(_out);
    }

    [Fact]
    public void Problem_that_lasts_is_released_then_the_all_clear_follows()
    {
        using var h = Make(60);
        h.Handle(E("OnBattery"));
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(["nas:OnBattery"], _out);
        h.Handle(E("OnLine"));
        Assert.Equal(["nas:OnBattery", "nas:OnLine"], _out);
    }

    [Theory]
    [InlineData("CommunicationLost", "CommunicationRestored")]
    [InlineData("Overload", "OverloadCleared")]
    [InlineData("DataStale", "DataRestored")]
    public void Every_paired_problem_can_be_held(string start, string clear)
    {
        using var h = Make(10);
        h.Handle(E(start));
        h.Handle(E(clear));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(_out);
    }

    [Fact]
    public void Urgent_events_are_never_delayed_and_release_what_is_held_first()
    {
        using var h = Make(300);
        h.Handle(E("OnBattery"));
        h.Handle(E("LowBattery"));
        Assert.Equal(["nas:OnBattery", "nas:LowBattery"], _out);
    }

    [Fact]
    public void Each_ups_is_held_on_its_own()
    {
        using var h = Make(60);
        h.Handle(E("OnBattery", "nas"));
        h.Handle(E("OnBattery", "office"));
        h.Handle(E("OnLine", "nas"));
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(["office:OnBattery"], _out);
    }

    [Fact]
    public void Machine_events_pass_without_releasing_other_ups()
    {
        using var h = Make(60);
        h.Handle(E("OnBattery", "nas"));
        h.Handle(E("ShutdownInitiated", "this-machine"));
        Assert.Equal(["this-machine:ShutdownInitiated"], _out);
    }
}
