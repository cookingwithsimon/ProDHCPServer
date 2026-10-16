using ProDHCPServer.Core.Adapters;

namespace ProDHCPServer.Core.Tests;

public class ReceiveWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Flags_a_burst_then_freeze_while_still_sending()
    {
        var w = new ReceiveWatch();
        Assert.Null(w.Update(T0, true, 588, 1000));
        Assert.Null(w.Update(T0.AddSeconds(6), true, 5846, 2000));   // burst after link-up
        Assert.Null(w.Update(T0.AddSeconds(20), true, 5846, 4000)); // frozen 14 s: not yet
        var stalled = w.Update(T0.AddSeconds(27), true, 5846, 5000);
        Assert.Equal(TimeSpan.FromSeconds(21), stalled);
        Assert.Equal(stalled, w.StalledFor);
    }

    [Fact]
    public void Clears_as_soon_as_anything_arrives()
    {
        var w = new ReceiveWatch();
        w.Update(T0, true, 100, 100);
        Assert.NotNull(w.Update(T0.AddSeconds(30), true, 100, 900));
        Assert.Null(w.Update(T0.AddSeconds(31), true, 160, 950));
        Assert.Null(w.StalledFor);
    }

    [Fact]
    public void Quiet_in_both_directions_is_not_a_stall()
    {
        var w = new ReceiveWatch();
        w.Update(T0, true, 100, 100);
        Assert.Null(w.Update(T0.AddSeconds(60), true, 100, 100));
    }

    [Fact]
    public void Link_down_or_missing_counters_reset_the_watch()
    {
        var w = new ReceiveWatch();
        w.Update(T0, true, 100, 100);
        Assert.NotNull(w.Update(T0.AddSeconds(30), true, 100, 500));
        Assert.Null(w.Update(T0.AddSeconds(31), false, 100, 500));
        Assert.Null(w.Update(T0.AddSeconds(32), true, 100, 600));    // counting restarts at link-up
        Assert.Null(w.Update(T0.AddSeconds(40), true, null, null));
    }
}
