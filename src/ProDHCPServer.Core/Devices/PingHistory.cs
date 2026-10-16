using ProDHCPServer.Core.Status;

namespace ProDHCPServer.Core.Devices;

/// <summary>Latency and loss over a window of pings.</summary>
public sealed record PingStats(int Sent, int Lost, double? MinMs, double? AvgMs, double? MaxMs)
{
    public int Received => Sent - Lost;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    /// <summary>Like "4 ms avg (1 to 12 ms), 0% loss" or "no replies".</summary>
    public string Describe()
    {
        if (Sent == 0) return "No pings yet";
        if (AvgMs is not { } avg) return $"No replies to {Sent} pings";
        return $"{Ms(avg)} avg ({Ms(MinMs!.Value)} to {Ms(MaxMs!.Value)}), {LossPercent:0.#}% loss";
    }

    public static string Ms(double ms) => ms < 1 ? "<1 ms" : $"{ms:0} ms";
}

/// <summary>A device stopped answering, or started answering again after an outage.</summary>
public sealed record PingTransition(bool Answering, DateTimeOffset At, TimeSpan? OutageLength);

/// <summary>
/// The last few minutes of pings to one device, for the latency graph, the loss figure and the
/// "stopped answering" log lines. Thread-safe: the pinger adds from a worker thread, the UI reads.
/// </summary>
public sealed class PingHistory
{
    /// <summary>Misses in a row before a device that was answering counts as down.</summary>
    public const int DownAfter = 3;

    private readonly object _gate = new();
    private readonly Queue<ProbeResult> _samples = new();
    private readonly int _capacity;
    private bool _answering;
    private bool _everAnswered;
    private DateTimeOffset? _downSince;

    public PingHistory(int capacity = 300) => _capacity = capacity;

    public ProbeResult? Last { get; private set; }
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Records one ping. Returns a transition when the device goes down or comes back.</summary>
    public PingTransition? Add(ProbeResult result)
    {
        lock (_gate)
        {
            _samples.Enqueue(result);
            while (_samples.Count > _capacity) _samples.Dequeue();
            Last = result;
            if (result.Success)
            {
                ConsecutiveFailures = 0;
                var wasDown = _everAnswered && !_answering;
                var outage = wasDown && _downSince is { } since ? result.At - since : (TimeSpan?)null;
                _answering = true;
                _everAnswered = true;
                _downSince = null;
                return wasDown ? new PingTransition(true, result.At, outage) : null;
            }

            ConsecutiveFailures++;
            if (ConsecutiveFailures == 1) _downSince = result.At;
            if (_answering && ConsecutiveFailures == DownAfter)
            {
                _answering = false;
                return new PingTransition(false, _downSince ?? result.At, null);
            }
            return null;
        }
    }

    public IReadOnlyList<ProbeResult> Snapshot()
    {
        lock (_gate) return _samples.ToList();
    }

    /// <summary>Stats for the pings sent within <paramref name="window"/> of <paramref name="now"/>.</summary>
    public PingStats Stats(DateTimeOffset now, TimeSpan window)
    {
        lock (_gate) return Compute(_samples.Where(s => now - s.At <= window));
    }

    public static PingStats Compute(IEnumerable<ProbeResult> samples)
    {
        int sent = 0, lost = 0;
        double min = double.MaxValue, max = 0, sum = 0;
        foreach (var s in samples)
        {
            sent++;
            if (!s.Success || s.RoundTripMs is not { } ms) { lost++; continue; }
            min = Math.Min(min, ms);
            max = Math.Max(max, ms);
            sum += ms;
        }
        int received = sent - lost;
        return received == 0
            ? new PingStats(sent, lost, null, null, null)
            : new PingStats(sent, lost, min, sum / received, max);
    }
}
