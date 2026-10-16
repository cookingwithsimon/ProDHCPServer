using System.Net;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Tests;

internal sealed class TestClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;
    public void Advance(TimeSpan by) => Now += by;
}

internal static class Packets
{
    public static readonly MacAddress MacA = MacAddress.Parse("00:1D:C1:11:22:33");
    public static readonly MacAddress MacB = MacAddress.Parse("00:60:74:AA:BB:CC");
    public static readonly IPAddress Server = IPv4.Parse("192.168.100.1");

    public static DhcpServerConfig Config(Func<DhcpServerConfig, DhcpServerConfig>? tweak = null)
    {
        var c = DhcpServerConfig.Default with
        {
            Router = IPv4.Parse("192.168.100.254"),
            DnsServers = [IPv4.Parse("192.168.100.1")],
        };
        return tweak is null ? c : tweak(c);
    }

    public static DhcpPacket Client(DhcpMessageType type, MacAddress mac, uint xid = 0x1234ABCD, bool broadcast = false,
        IPAddress? ciaddr = null, IPAddress? requested = null, IPAddress? serverId = null, string? hostName = null, IPAddress? giaddr = null)
    {
        var p = new DhcpPacket
        {
            Op = BootOp.Request,
            TransactionId = xid,
            IsBroadcastFlagSet = broadcast,
            ClientIp = ciaddr ?? IPAddress.Any,
            RelayIp = giaddr ?? IPAddress.Any,
        };
        mac.ToBytes().CopyTo(p.ClientHardwareAddress, 0);
        p.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)type));
        if (requested is not null) p.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.RequestedIp, requested));
        if (serverId is not null) p.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.ServerIdentifier, serverId));
        if (hostName is not null) p.Options.Add(DhcpOption.FromString(DhcpOptionCode.HostName, hostName));
        return p;
    }

    /// <summary>Sends through serialize → parse, the way a real packet arrives.</summary>
    public static DhcpReply? Send(this DhcpServerEngine engine, DhcpPacket packet) =>
        engine.Handle(DhcpPacket.Parse(packet.Serialize()));

    /// <summary>Runs DISCOVER → OFFER → REQUEST → ACK and returns the ACK.</summary>
    public static DhcpReply Dora(this DhcpServerEngine engine, MacAddress mac, string? hostName = null, uint xid = 1)
    {
        var offer = engine.Send(Client(DhcpMessageType.Discover, mac, xid, hostName: hostName))!;
        Assert.Equal(DhcpMessageType.Offer, offer.Packet.MessageType);
        var ack = engine.Send(Client(DhcpMessageType.Request, mac, xid, requested: offer.Packet.YourIp, serverId: Server, hostName: hostName))!;
        Assert.Equal(DhcpMessageType.Ack, ack.Packet.MessageType);
        return ack;
    }
}
