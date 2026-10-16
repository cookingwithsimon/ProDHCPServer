using System.Net;
using System.Net.NetworkInformation;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Devices;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.Windows;

/// <summary>A device to keep pinging: its MAC (the key), current address and the name used in the log.</summary>
public sealed record MonitoredDevice(MacAddress Mac, IPAddress Ip, string Name);

/// <summary>
/// Pings every device that holds a lease, once a second, and keeps each one's history for the latency
/// graph. Logs when a device stops answering and when it comes back, so drop-outs end up in the job report.
/// </summary>
public sealed class DeviceMonitor : IAsyncDisposable
{
    /// <summary>More than this many devices and the rest aren't pinged (a big switch full of gear).</summary>
    public const int MaxDevices = 64;

    private readonly SessionLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<MacAddress, DevicePinger> _pingers = [];
    private readonly Dictionary<MacAddress, string> _names = [];

    public DeviceMonitor(SessionLog log) => _log = log;

    public PingHistory? History(MacAddress mac)
    {
        lock (_gate) return _pingers.TryGetValue(mac, out var p) ? p.History : null;
    }

    /// <summary>Starts pinging new devices, follows address changes and stops pinging devices that are gone.</summary>
    public void Sync(IReadOnlyList<MonitoredDevice> devices)
    {
        var stale = new List<DevicePinger>();
        lock (_gate)
        {
            var wanted = devices.Take(MaxDevices).ToDictionary(d => d.Mac);
            foreach (var (mac, pinger) in _pingers.ToList())
            {
                if (wanted.TryGetValue(mac, out var d) && d.Ip.Equals(pinger.Target)) continue;
                stale.Add(pinger);
                _pingers.Remove(mac);
            }
            foreach (var d in wanted.Values)
            {
                _names[d.Mac] = d.Name;
                if (_pingers.ContainsKey(d.Mac)) continue;
                var mac = d.Mac;
                _pingers[mac] = new DevicePinger(d.Ip, t => OnTransition(mac, d.Ip, t));
            }
        }
        foreach (var p in stale) _ = p.DisposeAsync().AsTask();
    }

    private void OnTransition(MacAddress mac, IPAddress ip, PingTransition t)
    {
        string name;
        lock (_gate) name = _names.GetValueOrDefault(mac) ?? mac.ToString();
        if (t.Answering)
            _log.Add(LogSeverity.Success, LogCategory.Device,
                $"{name} ({ip}) is answering ping again after {Duration(t.OutageLength ?? TimeSpan.Zero)} without replies.");
        else
            _log.Add(LogSeverity.Warning, LogCategory.Device,
                $"{name} ({ip}) stopped answering ping at {t.At:HH:mm:ss}.");
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";

    public async Task ClearAsync()
    {
        List<DevicePinger> all;
        lock (_gate)
        {
            all = _pingers.Values.ToList();
            _pingers.Clear();
            _names.Clear();
        }
        await Task.WhenAll(all.Select(p => p.DisposeAsync().AsTask()));
    }

    public async ValueTask DisposeAsync() => await ClearAsync();
}

/// <summary>Pings one address once a second and records the results.</summary>
public sealed class DevicePinger : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<PingTransition> _onTransition;
    private readonly Task _loop;

    public DevicePinger(IPAddress target, Action<PingTransition> onTransition)
    {
        Target = target;
        _onTransition = onTransition;
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    public IPAddress Target { get; }
    public PingHistory History { get; } = new();

    private async Task Loop(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.Now;
            ProbeResult result;
            try
            {
                var reply = await ping.SendPingAsync(Target, Interval, cancellationToken: ct);
                result = reply.Status == IPStatus.Success
                    ? new ProbeResult(started, true, reply.RoundtripTime)
                    : new ProbeResult(started, false, null, $"Ping {Target}: {reply.Status}");
            }
            catch (OperationCanceledException) { return; }
            catch (PingException ex)
            {
                result = new ProbeResult(started, false, null, ex.InnerException?.Message ?? ex.Message);
            }
            if (History.Add(result) is { } transition) _onTransition(transition);

            // Keep a steady once-a-second rhythm whether the reply was quick or timed out.
            var wait = Interval - (DateTimeOffset.Now - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { /* cancelled */ }
        _cts.Dispose();
    }
}
