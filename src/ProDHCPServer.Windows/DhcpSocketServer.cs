using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

public sealed record TransportProblem(string Message, string? Detail);

/// <summary>
/// UDP 67 transport for <see cref="DhcpServerEngine"/>. The socket is bound to the server's
/// own address, so Windows only delivers traffic from that adapter and sends replies (including
/// 255.255.255.255 broadcasts) out of it. Every packet is also checked against the adapter's
/// interface index as a second guard.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DhcpSocketServer : IAsyncDisposable
{
    private const int IpUnicastIf = 31;
    private readonly DhcpServerEngine _engine;
    private readonly IPAddress _bindIp;
    private readonly int _interfaceIndex;
    private readonly NeighborTable _neighbors = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Timer? _tick;

    public DhcpSocketServer(DhcpServerEngine engine, int interfaceIndex)
    {
        _engine = engine;
        _bindIp = engine.Config.ServerIp;
        _interfaceIndex = interfaceIndex;
    }

    /// <summary>Problems that don't stop the server (bad packet, send failed). Raised on a worker thread.</summary>
    public event Action<TransportProblem>? Problem;

    public bool IsListening => _socket is not null;

    /// <summary>
    /// Binds UDP 67. Throws <see cref="TransportBindException"/> with a plain-English reason when it can't,
    /// with <see cref="TransportBindException.AddressNotReady"/> set if Windows hasn't applied the IP yet.
    /// </summary>
    public void Start()
    {
        if (_socket is not null) return;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.EnableBroadcast = true;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(_interfaceIndex));
            // Stop Windows reporting ICMP port-unreachable from a previous send as an error on the next receive.
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(_bindIp, DhcpPorts.Server));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse or SocketError.AccessDenied => new TransportBindException(
                    PortOwner.Describe(DhcpPorts.Server) ?? "UDP port 67 is already in use by another program (another DHCP server, or Windows Internet Connection Sharing). Close it and try again.",
                    ex.ToString(), addressNotReady: false),
                SocketError.AddressNotAvailable => new TransportBindException(
                    $"Windows hasn't applied {_bindIp} to the adapter yet.", ex.ToString(), addressNotReady: true),
                _ => new TransportBindException($"Couldn't open UDP port 67: {ex.Message}", ex.ToString(), addressNotReady: false),
            };
        }

        _socket = socket;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoop(socket, _cts.Token));
        _tick = new Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void SafeTick()
    {
        try { _engine.Tick(); }
        catch (Exception ex) { Problem?.Invoke(new TransportProblem("Lease timer error.", ex.ToString())); }
    }

    private async Task ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult r;
            try
            {
                r = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException ex)
            {
                Problem?.Invoke(new TransportProblem($"The DHCP socket stopped receiving: {ex.Message}", ex.ToString()));
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            // Safety: never answer anything that arrived on another adapter. (Packets this machine sends to
            // itself are looped back internally and may report the loopback interface; those are harmless.)
            bool fromSelf = r.RemoteEndPoint is IPEndPoint { Address: var src } && src.Equals(_bindIp);
            if (r.PacketInformation.Interface != 0 && r.PacketInformation.Interface != _interfaceIndex && !fromSelf)
                continue;

            DhcpPacket request;
            try
            {
                request = DhcpPacket.Parse(buffer.AsSpan(0, r.ReceivedBytes));
            }
            catch (DhcpParseException ex)
            {
                Problem?.Invoke(new TransportProblem($"Ignored a malformed DHCP packet from {r.RemoteEndPoint}: {ex.Message}",
                    Convert.ToHexString(buffer, 0, r.ReceivedBytes)));
                continue;
            }

            try
            {
                if (_engine.Handle(request) is { } reply) Send(socket, reply);
            }
            catch (Exception ex)
            {
                Problem?.Invoke(new TransportProblem($"Error answering {request.Summary()}: {ex.Message}", ex.ToString()));
            }
        }
    }

    private void Send(Socket socket, DhcpReply reply)
    {
        var bytes = reply.Packet.Serialize();
        var dest = reply.Destination;
        IPEndPoint target;
        switch (dest.Kind)
        {
            case ReplyKind.UnicastToHardwareAddress:
                // The client has no IP yet, so Windows can't ARP for it: add the neighbor entry ourselves.
                if (_neighbors.Set(_interfaceIndex, dest.Address, dest.ClientMac) is { } error)
                {
                    Problem?.Invoke(new TransportProblem($"Couldn't add an ARP entry for {dest.ClientMac}; sent the reply as a broadcast instead.", error));
                    target = new IPEndPoint(IPAddress.Broadcast, DhcpPorts.Client);
                }
                else target = new IPEndPoint(dest.Address, dest.Port);
                break;
            case ReplyKind.Broadcast:
                target = new IPEndPoint(IPAddress.Broadcast, DhcpPorts.Client);
                break;
            default:
                target = new IPEndPoint(dest.Address, dest.Port);
                break;
        }
        try
        {
            socket.SendTo(bytes, target);
        }
        catch (SocketException ex)
        {
            Problem?.Invoke(new TransportProblem($"Couldn't send {reply.Packet.MessageType} to {target}: {ex.Message}", ex.ToString()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tick?.Dispose();
        _cts?.Cancel();
        _socket?.Dispose();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ } }
        _neighbors.Clear();
        _cts?.Dispose();
        _socket = null;
    }
}

public sealed class TransportBindException(string message, string detail, bool addressNotReady) : Exception(message)
{
    public string Detail { get; } = detail;
    public bool AddressNotReady { get; } = addressNotReady;
}

/// <summary>Sends a probe DISCOVER and collects OFFERs from any other DHCP server on the link.</summary>
[SupportedOSPlatform("windows")]
public static class OtherServerDetector
{
    public sealed record Result(ForeignDhcpServer? Found, string? Limitation);

    public static async Task<Result> ProbeAsync(IPAddress localIp, int interfaceIndex, MacAddress adapterMac, IPAddress ourServerIp, TimeSpan listenFor, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // The Windows DHCP client service also uses port 68, so share it.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(interfaceIndex));
            socket.Bind(new IPEndPoint(localIp, DhcpPorts.Client));
        }
        catch (SocketException ex)
        {
            return new Result(null, $"Couldn't listen on UDP 68 to check for other DHCP servers ({ex.Message}). Relying on the passive check only.");
        }

        uint xid = (uint)Random.Shared.Next();
        var probe = RogueServerProbe.BuildDiscover(adapterMac, xid).Serialize();
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(listenFor);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { socket.SendTo(probe, new IPEndPoint(IPAddress.Broadcast, DhcpPorts.Server)); }
            catch (SocketException ex) { return new Result(null, $"Couldn't send the probe DISCOVER ({ex.Message})."); }

            using var round = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            round.CancelAfter(listenFor / 2);
            while (!round.IsCancellationRequested)
            {
                try
                {
                    var r = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, round.Token);
                    if (r.PacketInformation.Interface != 0 && r.PacketInformation.Interface != interfaceIndex) continue;
                    DhcpPacket reply;
                    try { reply = DhcpPacket.Parse(buffer.AsSpan(0, r.ReceivedBytes)); }
                    catch (DhcpParseException) { continue; }
                    var source = (r.RemoteEndPoint as IPEndPoint)?.Address;
                    if (RogueServerProbe.Classify(reply, xid, ourServerIp, source) is { } found)
                        return new Result(found, null);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            }
            if (ct.IsCancellationRequested) break;
        }
        return new Result(null, null);
    }
}
