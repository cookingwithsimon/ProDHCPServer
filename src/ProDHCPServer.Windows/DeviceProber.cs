using System.Net;
using System.Net.Sockets;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.Windows;

/// <summary>Checks the focused device's web ports every few seconds. Pings come from <see cref="DeviceMonitor"/>.</summary>
public sealed class DeviceProber : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _portLoop;

    public DeviceProber(IPAddress target)
    {
        Target = target;
        _portLoop = Task.Run(() => PortLoop(_cts.Token));
    }

    public IPAddress Target { get; }
    public WebUiProbe? LastWebUi { get; private set; }

    public event Action? Updated;

    private async Task PortLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var http = TcpOpen(80, ct);
            var https = TcpOpen(443, ct);
            var (h, hs) = (await http, await https);
            if (ct.IsCancellationRequested) return;
            LastWebUi = new WebUiProbe(DateTimeOffset.Now, h.Open, hs.Open, $"Port 80: {h.Detail}{Environment.NewLine}Port 443: {hs.Detail}");
            Updated?.Invoke();
            // Check often until something answers, then settle down.
            var wait = h.Open || hs.Open ? 15_000 : 4_000;
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public static async Task<(bool Open, string Detail)> TcpOpen(IPAddress ip, int port, CancellationToken ct, int timeoutMs = 1500)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, timeout.Token);
            return (true, "open");
        }
        catch (OperationCanceledException) { return (false, $"no answer within {timeoutMs} ms"); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return (false, "closed (connection refused)"); }
        catch (SocketException ex) { return (false, ex.Message); }
    }

    private Task<(bool Open, string Detail)> TcpOpen(int port, CancellationToken ct) => TcpOpen(Target, port, ct);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _portLoop; } catch { /* cancelled */ }
        _cts.Dispose();
    }
}
