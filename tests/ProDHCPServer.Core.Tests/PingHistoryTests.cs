using ProDHCPServer.Core.Devices;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.Core.Tests;

public class PingHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static ProbeResult Ok(int second, double ms) => new(T0.AddSeconds(second), true, ms);
    private static ProbeResult Miss(int second) => new(T0.AddSeconds(second), false, null, "TimedOut");

    [Fact]
    public void Stats_cover_only_the_window()
    {
        var h = new PingHistory();
        h.Add(Ok(0, 100)); // outside a 10 s window at second 20
        h.Add(Ok(15, 2));
        h.Add(Miss(16));
        h.Add(Ok(17, 4));
        h.Add(Ok(18, 6));

        var s = h.Stats(T0.AddSeconds(20), TimeSpan.FromSeconds(10));
        Assert.Equal(4, s.Sent);
        Assert.Equal(1, s.Lost);
        Assert.Equal(25, s.LossPercent);
        Assert.Equal(2, s.MinMs);
        Assert.Equal(4, s.AvgMs);
        Assert.Equal(6, s.MaxMs);
        Assert.Equal("4 ms avg (2 ms to 6 ms), 25% loss", s.Describe());
    }

    [Fact]
    public void Stats_with_no_replies_say_so()
    {
        var s = PingHistory.Compute([Miss(0), Miss(1)]);
        Assert.Null(s.AvgMs);
        Assert.Equal(100, s.LossPercent);
        Assert.Equal("No replies to 2 pings", s.Describe());
        Assert.Equal("No pings yet", PingHistory.Compute([]).Describe());
        Assert.Equal("<1 ms", PingStats.Ms(0.4));
    }

    [Fact]
    public void Keeps_only_the_newest_samples()
    {
        var h = new PingHistory(capacity: 3);
        for (int i = 0; i < 5; i++) h.Add(Ok(i, i));
        Assert.Equal([2.0, 3.0, 4.0], h.Snapshot().Select(s => s.RoundTripMs!.Value));
    }

    [Fact]
    public void Reports_down_after_three_misses_and_back_up_with_outage_length()
    {
        var h = new PingHistory();
        Assert.Null(h.Add(Ok(0, 1)));
        Assert.Null(h.Add(Miss(1)));
        Assert.Null(h.Add(Miss(2)));
        var down = h.Add(Miss(3));
        Assert.NotNull(down);
        Assert.False(down.Answering);
        Assert.Equal(T0.AddSeconds(1), down.At);
        Assert.Null(h.Add(Miss(4)));
        Assert.Equal(4, h.ConsecutiveFailures);

        var up = h.Add(Ok(6, 2));
        Assert.NotNull(up);
        Assert.True(up.Answering);
        Assert.Equal(TimeSpan.FromSeconds(5), up.OutageLength);
        Assert.Equal(0, h.ConsecutiveFailures);
    }

    [Fact]
    public void A_short_blip_or_a_device_that_never_answered_is_not_reported()
    {
        var h = new PingHistory();
        h.Add(Ok(0, 1));
        h.Add(Miss(1));
        h.Add(Miss(2));
        Assert.Null(h.Add(Ok(3, 1)));

        var never = new PingHistory();
        for (int i = 0; i < 5; i++) Assert.Null(never.Add(Miss(i)));
        // First reply from a device that was still booting is not a "back up" event.
        Assert.Null(never.Add(Ok(6, 1)));
    }
}
