namespace ProDHCPServer.Core.Adapters;

/// <summary>
/// Spots an adapter that still reports "link up" but has stopped receiving: the receive counter
/// stands still while this laptop keeps sending. Some USB adapters (notably Realtek RTL8153 on old
/// drivers) fail this way after a few seconds, which otherwise looks exactly like a silent device.
/// </summary>
public sealed class ReceiveWatch
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(20);

    private long? _lastReceived;
    private long _sentAtLastReceive;
    private DateTimeOffset _lastReceiveChange;

    /// <summary>How long nothing has been received while sending continued, once that passes <see cref="Threshold"/>; otherwise null.</summary>
    public TimeSpan? StalledFor { get; private set; }

    public TimeSpan? Update(DateTimeOffset now, bool linkUp, long? bytesReceived, long? bytesSent)
    {
        if (!linkUp || bytesReceived is not { } received || bytesSent is not { } sent)
        {
            Reset();
            return null;
        }
        if (_lastReceived != received)
        {
            _lastReceived = received;
            _sentAtLastReceive = sent;
            _lastReceiveChange = now;
            return StalledFor = null;
        }
        var quiet = now - _lastReceiveChange;
        return StalledFor = sent > _sentAtLastReceive && quiet >= Threshold ? quiet : null;
    }

    public void Reset()
    {
        _lastReceived = null;
        StalledFor = null;
    }
}
