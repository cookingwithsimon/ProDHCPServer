using System.Buffers.Binary;
using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Discovery;

/// <summary>An IPv4 packet as a raw socket hands it over (no Ethernet header).</summary>
public readonly ref struct IPv4Packet
{
    public const byte Icmp = 1;
    public const byte Igmp = 2;
    public const byte Tcp = 6;
    public const byte Udp = 17;

    public IPAddress Source { get; private init; }
    public IPAddress Destination { get; private init; }
    public byte Protocol { get; private init; }
    /// <summary>True for the second and later pieces of a fragmented packet, which have no UDP header.</summary>
    public bool IsLaterFragment { get; private init; }
    public ReadOnlySpan<byte> Payload { get; private init; }

    public static bool TryParse(ReadOnlySpan<byte> data, out IPv4Packet packet)
    {
        packet = default;
        if (data.Length < 20 || data[0] >> 4 != 4) return false;
        int headerLength = (data[0] & 0x0F) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (headerLength < 20 || total < headerLength || data.Length < headerLength) return false;
        total = Math.Min(total, data.Length);
        int fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) & 0x1FFF;
        packet = new IPv4Packet
        {
            Protocol = data[9],
            Source = new IPAddress(data.Slice(12, 4)),
            Destination = new IPAddress(data.Slice(16, 4)),
            IsLaterFragment = fragmentOffset != 0,
            Payload = data[headerLength..total],
        };
        return true;
    }

    /// <summary>The UDP ports and payload, when this is the first (or only) piece of a UDP packet.</summary>
    public bool TryGetUdp(out int sourcePort, out int destinationPort, out ReadOnlySpan<byte> payload)
    {
        sourcePort = destinationPort = 0;
        payload = default;
        if (Protocol != Udp || IsLaterFragment || Payload.Length < 8) return false;
        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(Payload);
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(Payload[2..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(Payload[4..]);
        payload = Payload[8..Math.Clamp(length, 8, Payload.Length)];
        return true;
    }
}

/// <summary>An Ethernet II frame (or an 802.3 frame with an LLC/SNAP header, as CDP uses).</summary>
public readonly ref struct EthernetFrame
{
    public const ushort Arp = 0x0806;
    public const ushort IPv4 = 0x0800;
    public const ushort Lldp = 0x88CC;
    public const ushort Vlan = 0x8100;

    public MacAddress Destination { get; private init; }
    public MacAddress Source { get; private init; }
    /// <summary>The EtherType, or 0 for an 802.3 length frame (see <see cref="SnapOui"/>).</summary>
    public ushort EtherType { get; private init; }
    /// <summary>For 802.3 frames with LLC/SNAP: the organisation code (Cisco is 0x00000C).</summary>
    public uint SnapOui { get; private init; }
    /// <summary>For 802.3 frames with LLC/SNAP: the protocol ID (CDP is 0x2000).</summary>
    public ushort SnapProtocol { get; private init; }
    public ReadOnlySpan<byte> Payload { get; private init; }

    public static bool TryParse(ReadOnlySpan<byte> data, out EthernetFrame frame)
    {
        frame = default;
        if (data.Length < 14) return false;
        int offset = 12;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        if (type == Vlan && data.Length >= 18)
        {
            offset += 4;
            type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        }
        offset += 2;
        var dst = MacAddress.FromBytes(data);
        var src = MacAddress.FromBytes(data[6..]);
        if (type >= 0x0600)
        {
            frame = new EthernetFrame { Destination = dst, Source = src, EtherType = type, Payload = data[offset..] };
            return true;
        }
        // 802.3 length field: LLC (AA AA 03) then SNAP (3-byte OUI, 2-byte protocol).
        var llc = data[offset..Math.Min(data.Length, offset + type)];
        if (llc.Length < 8 || llc[0] != 0xAA || llc[1] != 0xAA || llc[2] != 0x03) return false;
        frame = new EthernetFrame
        {
            Destination = dst,
            Source = src,
            SnapOui = (uint)(llc[3] << 16 | llc[4] << 8 | llc[5]),
            SnapProtocol = BinaryPrimitives.ReadUInt16BigEndian(llc[6..]),
            Payload = llc[8..],
        };
        return true;
    }

    public bool IsCdp => EtherType == 0 && SnapOui == 0x00000C && SnapProtocol == 0x2000;
}

/// <summary>An IPv4-over-Ethernet ARP packet.</summary>
public sealed record ArpPacket(int Operation, MacAddress SenderMac, IPAddress SenderIp, MacAddress TargetMac, IPAddress TargetIp)
{
    public const int Request = 1;
    public const int Reply = 2;

    /// <summary>
    /// An ARP probe (RFC 5227): the sender has no address yet and is checking that <see cref="TargetIp"/> is free.
    /// A device with a static IP sends these at boot, so the target is the address it is about to use.
    /// </summary>
    public bool IsProbe => Operation == Request && IPv4.IsZero(SenderIp);

    /// <summary>A gratuitous ARP: the sender announcing its own address.</summary>
    public bool IsAnnouncement => !IPv4.IsZero(SenderIp) && SenderIp.Equals(TargetIp);

    /// <summary>The address this packet shows the sender is using (or about to use, for a probe).</summary>
    public IPAddress ClaimedIp => IsProbe ? TargetIp : SenderIp;

    public static ArpPacket? TryParse(ReadOnlySpan<byte> data)
    {
        // Hardware type 1 (Ethernet), protocol 0x0800 (IPv4), lengths 6 and 4.
        if (data.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(data) != 1 || BinaryPrimitives.ReadUInt16BigEndian(data[2..]) != 0x0800
            || data[4] != 6 || data[5] != 4) return null;
        return new ArpPacket(
            BinaryPrimitives.ReadUInt16BigEndian(data[6..]),
            MacAddress.FromBytes(data[8..]), new IPAddress(data.Slice(14, 4)),
            MacAddress.FromBytes(data[18..]), new IPAddress(data.Slice(24, 4)));
    }
}
