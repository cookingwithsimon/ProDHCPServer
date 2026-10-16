using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace ProDHCPServer.Windows;

/// <summary>
/// Receives every IPv4 packet addressed to this laptop on one adapter (including broadcasts and joined
/// multicast groups) with a raw socket in SIO_RCVALL "IP level" mode. Unlike promiscuous mode this never
/// sees other devices' unicast or unjoined multicast, so a busy video stream on the switch can't flood it.
/// A device with a static IP on another subnet shows up here as soon as it broadcasts anything.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawIpSniffer : IAsyncDisposable
{
    private const int RcvallIpLevel = 3;
    private readonly IPAddress _bindIp;
    private readonly Action<byte[], int> _onPacket;
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RawIpSniffer(IPAddress bindIp, Action<byte[], int> onPacket)
    {
        _bindIp = bindIp;
        _onPacket = onPacket;
    }

    /// <summary>Starts listening; returns a plain-English problem instead of throwing.</summary>
    public string? Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
        try
        {
            socket.Bind(new IPEndPoint(_bindIp, 0));
            socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(RcvallIpLevel), null);
            socket.ReceiveBufferSize = 1 << 20;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            return $"Couldn't listen for broadcasts from devices on other subnets ({ex.Message}).";
        }
        _socket = socket;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(socket, _cts.Token));
        return null;
    }

    private async Task Loop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[65535];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            try { _onPacket(buffer, n); } catch { /* a bad packet must not stop listening */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _socket?.Dispose();
        if (_loop is not null) { try { await _loop; } catch { /* stopped */ } }
        _cts?.Dispose();
    }
}

/// <summary>
/// One UDP socket for a discovery protocol: bound to the adapter's address on the protocol's port when
/// Windows allows it (shared with Windows' own mDNS and SSDP services), joined to its multicast groups.
/// Falls back to a random port, which still gets replies sent straight back to it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiscoverySocket : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Action<IPEndPoint, int, byte[], int> _onPacket;

    private DiscoverySocket(Socket socket, int requestedPort, Action<IPEndPoint, int, byte[], int> onPacket)
    {
        _socket = socket;
        _onPacket = onPacket;
        RequestedPort = requestedPort;
        LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _loop = Task.Run(Loop);
    }

    public int RequestedPort { get; }
    public int LocalPort { get; }
    /// <summary>False when the port was in use and a random one was taken instead.</summary>
    public bool OnRequestedPort => RequestedPort == 0 || LocalPort == RequestedPort;

    /// <param name="onPacket">Called with the sender, the protocol's port, the buffer and the length. Worker thread.</param>
    public static DiscoverySocket Open(IPAddress localIp, int port, IEnumerable<IPAddress> groups, int ttl, Action<IPEndPoint, int, byte[], int> onPacket)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            // Stop Windows reporting ICMP port-unreachable from a previous send as an error on the next receive.
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            try { socket.Bind(new IPEndPoint(localIp, port)); }
            catch (SocketException) when (port != 0) { socket.Bind(new IPEndPoint(localIp, 0)); }
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localIp.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
            foreach (var group in groups)
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, localIp)); }
                catch (SocketException) { /* already joined by another socket on this interface: still received */ }
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new DiscoverySocket(socket, port, onPacket);
    }

    public void Send(IPAddress destination, int port, byte[] payload)
    {
        try { _socket.SendTo(payload, new IPEndPoint(destination, port)); }
        catch (SocketException) { /* link down or address going away; the next round tries again */ }
        catch (ObjectDisposedException) { }
    }

    private async Task Loop()
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            try { _onPacket((IPEndPoint)r.RemoteEndPoint, RequestedPort, buffer, r.ReceivedBytes); } catch { /* keep listening */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socket.Dispose();
        try { await _loop; } catch { /* stopped */ }
        _cts.Dispose();
    }
}
