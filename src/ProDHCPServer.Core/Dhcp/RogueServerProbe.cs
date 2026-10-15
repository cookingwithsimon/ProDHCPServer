using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Dhcp;

/// <summary>A DHCP server that answered our probe DISCOVER.</summary>
public sealed record ForeignDhcpServer(IPAddress ServerId, IPAddress? SourceIp, IPAddress OfferedIp, IPAddress? Router, string? Detail);

/// <summary>
/// Builds the probe DISCOVER sent before serving, and recognises OFFERs to it. Any OFFER from a
/// server that is not us means the link already has DHCP (for example a venue network).
/// </summary>
public static class RogueServerProbe
{
    public static DhcpPacket BuildDiscover(MacAddress probeMac, uint transactionId)
    {
        var p = new DhcpPacket
        {
            Op = BootOp.Request,
            TransactionId = transactionId,
            IsBroadcastFlagSet = true, // ask for broadcast replies: we have no usable address yet
        };
        probeMac.ToBytes().CopyTo(p.ClientHardwareAddress, 0);
        p.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)DhcpMessageType.Discover));
        p.Options.Add(new DhcpOption(DhcpOptionCode.ParameterRequestList,
            [DhcpOptionCode.SubnetMask, DhcpOptionCode.Router, DhcpOptionCode.DnsServers]));
        p.Options.Add(DhcpOption.FromString(DhcpOptionCode.HostName, "ProDHCPServer-probe"));
        return p;
    }

    /// <summary>Returns the foreign server if <paramref name="reply"/> is an OFFER to our probe from someone other than us.</summary>
    public static ForeignDhcpServer? Classify(DhcpPacket reply, uint transactionId, IPAddress? ourServerIp, IPAddress? sourceIp = null)
    {
        if (reply.Op != BootOp.Reply || reply.TransactionId != transactionId) return null;
        if (reply.MessageType is not (DhcpMessageType.Offer or DhcpMessageType.Ack)) return null;
        var serverId = reply.ServerIdentifier ?? sourceIp ?? reply.NextServerIp;
        if (ourServerIp is not null && serverId.Equals(ourServerIp)) return null;
        var router = reply.GetOption(DhcpOptionCode.Router)?.AsAddresses().FirstOrDefault();
        return new ForeignDhcpServer(serverId, sourceIp, reply.YourIp, router, reply.Describe());
    }
}
