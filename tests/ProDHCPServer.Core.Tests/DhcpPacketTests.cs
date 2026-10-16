using System.Net;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Tests;

public class DhcpPacketTests
{
    /// <summary>A DISCOVER assembled byte-by-byte (not via Serialize) so parsing is tested independently.</summary>
    private static byte[] RawDiscover()
    {
        var b = new byte[300];
        b[0] = 1; b[1] = 1; b[2] = 6; b[3] = 0;
        b[4] = 0x39; b[5] = 0x03; b[6] = 0xF3; b[7] = 0x26;   // xid
        b[10] = 0x80;                                         // broadcast flag
        byte[] mac = [0x00, 0x1D, 0xC1, 0x11, 0x22, 0x33];
        mac.CopyTo(b, 28);
        DhcpPacket.MagicCookie.CopyTo(b, 236);
        byte[] options =
        [
            53, 1, 1,                         // DISCOVER
            61, 7, 1, 0x00, 0x1D, 0xC1, 0x11, 0x22, 0x33,
            50, 4, 192, 168, 100, 150,        // requested IP
            12, 6, (byte)'d', (byte)'s', (byte)'p', (byte)'-', (byte)'1', 0,  // NUL-padded host name
            55, 4, 1, 3, 6, 15,
            255,
        ];
        options.CopyTo(b, 240);
        return b;
    }

    [Fact]
    public void Parses_hand_built_discover()
    {
        var p = DhcpPacket.Parse(RawDiscover());
        Assert.Equal(BootOp.Request, p.Op);
        Assert.Equal(0x3903F326u, p.TransactionId);
        Assert.True(p.IsBroadcastFlagSet);
        Assert.Equal(DhcpMessageType.Discover, p.MessageType);
        Assert.Equal(MacAddress.Parse("00:1D:C1:11:22:33"), p.ClientMac);
        Assert.Equal(IPv4.Parse("192.168.100.150"), p.RequestedIp);
        Assert.Equal("dsp-1", p.HostName);
        Assert.Equal(new byte[] { 1, 3, 6, 15 }, p.ParameterRequestList);
        Assert.Equal(7, p.ClientId!.Length);
    }

    [Fact]
    public void Round_trips_every_field()
    {
        var original = new DhcpPacket
        {
            Op = BootOp.Reply,
            Hops = 2,
            TransactionId = 0xDEADBEEF,
            Seconds = 7,
            Flags = DhcpPacket.BroadcastFlag,
            ClientIp = IPv4.Parse("10.0.0.5"),
            YourIp = IPv4.Parse("10.0.0.6"),
            NextServerIp = IPv4.Parse("10.0.0.1"),
            RelayIp = IPv4.Parse("10.0.0.254"),
            ServerName = "srv",
            BootFile = "boot.bin",
        };
        Packets.MacA.ToBytes().CopyTo(original.ClientHardwareAddress, 0);
        original.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)DhcpMessageType.Ack));
        original.Options.Add(DhcpOption.FromUInt32(DhcpOptionCode.LeaseTime, 3600));
        original.Options.Add(DhcpOption.FromAddresses(DhcpOptionCode.DnsServers, [IPv4.Parse("1.1.1.1"), IPv4.Parse("8.8.8.8")]));

        var bytes = original.Serialize();
        var parsed = DhcpPacket.Parse(bytes);

        Assert.True(bytes.Length >= DhcpPacket.BootpMinimumLength);
        Assert.Equal(original.Op, parsed.Op);
        Assert.Equal(original.Hops, parsed.Hops);
        Assert.Equal(original.TransactionId, parsed.TransactionId);
        Assert.Equal(original.Seconds, parsed.Seconds);
        Assert.Equal(original.Flags, parsed.Flags);
        Assert.Equal(original.ClientIp, parsed.ClientIp);
        Assert.Equal(original.YourIp, parsed.YourIp);
        Assert.Equal(original.NextServerIp, parsed.NextServerIp);
        Assert.Equal(original.RelayIp, parsed.RelayIp);
        Assert.Equal("srv", parsed.ServerName);
        Assert.Equal("boot.bin", parsed.BootFile);
        Assert.Equal(Packets.MacA, parsed.ClientMac);
        Assert.Equal(DhcpMessageType.Ack, parsed.MessageType);
        Assert.Equal(3600u, parsed.GetOption(DhcpOptionCode.LeaseTime)!.AsUInt32());
        Assert.Equal(2, parsed.GetOption(DhcpOptionCode.DnsServers)!.AsAddresses().Count);
    }

    [Fact]
    public void Concatenates_repeated_options_per_rfc3396()
    {
        var b = RawDiscover();
        byte[] options = [53, 1, 1, 43, 2, 0xAA, 0xBB, 12, 1, (byte)'x', 43, 1, 0xCC, 255];
        Array.Clear(b, 240, 60);
        options.CopyTo(b, 240);
        var p = DhcpPacket.Parse(b);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, p.GetOption(43)!.Data);
    }

    [Fact]
    public void Splits_options_longer_than_255_bytes()
    {
        var p = Packets.Client(DhcpMessageType.Discover, Packets.MacA);
        var big = Enumerable.Range(0, 600).Select(i => (byte)i).ToArray();
        p.Options.Add(new DhcpOption(43, big));
        var parsed = DhcpPacket.Parse(p.Serialize());
        Assert.Equal(big, parsed.GetOption(43)!.Data);
    }

    [Fact]
    public void Reads_options_from_overloaded_file_and_sname_fields()
    {
        var b = RawDiscover();
        Array.Clear(b, 240, 60);
        byte[] main = [53, 1, 3, 52, 1, 3, 255];
        main.CopyTo(b, 240);
        byte[] inFile = [50, 4, 10, 0, 0, 9, 255];
        inFile.CopyTo(b, 108);
        byte[] inSname = [12, 3, (byte)'c', (byte)'a', (byte)'m', 255];
        inSname.CopyTo(b, 44);

        var p = DhcpPacket.Parse(b);
        Assert.Equal(DhcpMessageType.Request, p.MessageType);
        Assert.Equal(IPv4.Parse("10.0.0.9"), p.RequestedIp);
        Assert.Equal("cam", p.HostName);
        Assert.Null(p.GetOption(DhcpOptionCode.OptionOverload));
        Assert.Equal("", p.BootFile);
    }

    [Fact]
    public void Rejects_short_packets()
    {
        var ex = Assert.Throws<DhcpParseException>(() => DhcpPacket.Parse(new byte[100]));
        Assert.Contains("at least 236", ex.Message);
    }

    [Fact]
    public void Rejects_option_running_past_the_end()
    {
        var b = RawDiscover()[..250];
        b[240] = 53; b[241] = 1; b[242] = 1;
        b[243] = 12; b[244] = 50; // claims 50 bytes, only 5 remain
        Assert.Throws<DhcpParseException>(() => DhcpPacket.Parse(b));
    }

    [Fact]
    public void Rejects_unknown_op_code()
    {
        var b = RawDiscover();
        b[0] = 9;
        Assert.Throws<DhcpParseException>(() => DhcpPacket.Parse(b));
    }

    [Fact]
    public void Tolerates_missing_end_option()
    {
        var b = RawDiscover()[..243];
        b[240] = 53; b[241] = 1; b[242] = 1;
        Assert.Equal(DhcpMessageType.Discover, DhcpPacket.Parse(b).MessageType);
    }

    [Fact]
    public void Treats_packet_without_magic_cookie_as_bootp()
    {
        var b = RawDiscover();
        Array.Clear(b, 236, 64);
        var p = DhcpPacket.Parse(b);
        Assert.False(p.HasMagicCookie);
        Assert.Null(p.MessageType);
        Assert.Empty(p.Options);
    }

    [Fact]
    public void Broadcast_flag_setter_only_touches_top_bit()
    {
        var p = new DhcpPacket { Flags = 0x0001 };
        p.IsBroadcastFlagSet = true;
        Assert.Equal(0x8001, p.Flags);
        p.IsBroadcastFlagSet = false;
        Assert.Equal(0x0001, p.Flags);
    }

    [Fact]
    public void Describe_lists_options_in_plain_text()
    {
        var text = DhcpPacket.Parse(RawDiscover()).Describe();
        Assert.Contains("DISCOVER from 00:1D:C1:11:22:33", text);
        Assert.Contains("Requested IP: 192.168.100.150", text);
        Assert.Contains("Host name: \"dsp-1\"", text);
    }

    [Fact]
    public void Max_message_size_is_read_big_endian()
    {
        var p = Packets.Client(DhcpMessageType.Discover, Packets.MacA);
        p.Options.Add(DhcpOption.FromUInt16(DhcpOptionCode.MaxMessageSize, 1500));
        Assert.Equal((ushort)1500, DhcpPacket.Parse(p.Serialize()).MaxMessageSize);
    }

    [Fact]
    public void Rogue_probe_recognises_foreign_offer_but_not_our_own()
    {
        var probe = RogueServerProbe.BuildDiscover(Packets.MacA, 0x55);
        Assert.True(probe.IsBroadcastFlagSet);

        var offer = new DhcpPacket { Op = BootOp.Reply, TransactionId = 0x55, YourIp = IPv4.Parse("10.10.0.77") };
        offer.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)DhcpMessageType.Offer));
        offer.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.ServerIdentifier, IPv4.Parse("10.10.0.1")));
        offer.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.Router, IPv4.Parse("10.10.0.254")));

        var foreign = RogueServerProbe.Classify(DhcpPacket.Parse(offer.Serialize()), 0x55, Packets.Server);
        Assert.NotNull(foreign);
        Assert.Equal(IPv4.Parse("10.10.0.1"), foreign!.ServerId);
        Assert.Equal(IPv4.Parse("10.10.0.77"), foreign.OfferedIp);
        Assert.Equal(IPv4.Parse("10.10.0.254"), foreign.Router);

        Assert.Null(RogueServerProbe.Classify(offer, 0x56, Packets.Server)); // other transaction
        offer.SetOption(DhcpOption.FromAddress(DhcpOptionCode.ServerIdentifier, Packets.Server));
        Assert.Null(RogueServerProbe.Classify(offer, 0x55, Packets.Server)); // that's us
    }
}
