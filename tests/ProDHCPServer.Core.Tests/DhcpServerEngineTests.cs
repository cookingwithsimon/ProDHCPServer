using System.Net;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using static ProDHCPServer.Core.Tests.Packets;

namespace ProDHCPServer.Core.Tests;

public class DhcpServerEngineTests
{
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly List<DhcpEngineEvent> _events = [];

    private DhcpServerEngine Engine(Func<DhcpServerConfig, DhcpServerConfig>? tweak = null)
    {
        var e = new DhcpServerEngine(Config(tweak), _clock);
        e.EventRaised += _events.Add;
        return e;
    }

    private static IPAddress Ip(string s) => IPv4.Parse(s);

    [Fact]
    public void Full_dora_gives_first_pool_address_with_all_options()
    {
        var engine = Engine();
        var offer = engine.Send(Client(DhcpMessageType.Discover, MacA, hostName: "dsp-1"))!;

        Assert.Equal(BootOp.Reply, offer.Packet.Op);
        Assert.Equal(Ip("192.168.100.100"), offer.Packet.YourIp);
        Assert.Equal(0x1234ABCDu, offer.Packet.TransactionId);
        Assert.Equal(Server, offer.Packet.ServerIdentifier);
        Assert.Equal(Ip("255.255.255.0"), offer.Packet.GetOption(DhcpOptionCode.SubnetMask)!.AsAddress());
        Assert.Equal(Ip("192.168.100.254"), offer.Packet.GetOption(DhcpOptionCode.Router)!.AsAddress());
        Assert.Equal(3600u, offer.Packet.GetOption(DhcpOptionCode.LeaseTime)!.AsUInt32());
        Assert.Equal(1800u, offer.Packet.GetOption(DhcpOptionCode.RenewalTime)!.AsUInt32());
        Assert.Equal(3150u, offer.Packet.GetOption(DhcpOptionCode.RebindingTime)!.AsUInt32());
        Assert.Equal(Ip("192.168.100.255"), offer.Packet.GetOption(DhcpOptionCode.BroadcastAddress)!.AsAddress());

        var ack = engine.Send(Client(DhcpMessageType.Request, MacA, requested: offer.Packet.YourIp, serverId: Server))!;
        Assert.Equal(DhcpMessageType.Ack, ack.Packet.MessageType);
        Assert.Equal(Ip("192.168.100.100"), ack.Packet.YourIp);

        var lease = Assert.Single(engine.GetLeases());
        Assert.Equal(LeaseState.Bound, lease.State);
        Assert.Equal("dsp-1", lease.HostName);
        Assert.Equal(_clock.Now + TimeSpan.FromHours(1), lease.Expires);
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.LeaseBound);
    }

    [Fact]
    public void Reply_survives_serialisation_and_is_at_least_300_bytes()
    {
        var reply = Engine().Send(Client(DhcpMessageType.Discover, MacA))!;
        var bytes = reply.Packet.Serialize();
        Assert.True(bytes.Length >= 300);
        Assert.Equal(DhcpMessageType.Offer, DhcpPacket.Parse(bytes).MessageType);
    }

    [Fact]
    public void Same_device_gets_same_address_again()
    {
        var engine = Engine();
        var first = engine.Dora(MacA).Packet.YourIp;
        engine.Send(Client(DhcpMessageType.Release, MacA, ciaddr: first, serverId: Server));
        engine.Dora(MacB);
        var again = engine.Dora(MacA, xid: 9).Packet.YourIp;
        Assert.Equal(first, again);
    }

    [Fact]
    public void Two_devices_get_different_addresses()
    {
        var engine = Engine();
        Assert.NotEqual(engine.Dora(MacA).Packet.YourIp, engine.Dora(MacB).Packet.YourIp);
    }

    [Fact]
    public void Honours_requested_address_in_pool()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA, requested: Ip("192.168.100.150")))!;
        Assert.Equal(Ip("192.168.100.150"), offer.Packet.YourIp);
    }

    [Fact]
    public void Ignores_requested_address_outside_pool()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA, requested: Ip("10.0.0.5")))!;
        Assert.Equal(Ip("192.168.100.100"), offer.Packet.YourIp);
    }

    [Fact]
    public void Never_hands_out_server_or_gateway_address()
    {
        var engine = Engine(c => c with { PoolStart = Ip("192.168.100.1"), PoolEnd = Ip("192.168.100.3"), Router = Ip("192.168.100.2") });
        Assert.Equal(Ip("192.168.100.3"), engine.Dora(MacA).Packet.YourIp);
        Assert.Null(engine.Send(Client(DhcpMessageType.Discover, MacB)));
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.PoolExhausted);
    }

    // --- RFC 2131 4.1 reply destinations ---

    [Fact]
    public void Broadcast_flag_means_broadcast_reply()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA, broadcast: true))!;
        Assert.Equal(ReplyKind.Broadcast, offer.Destination.Kind);
        Assert.Equal(IPAddress.Broadcast, offer.Destination.Address);
        Assert.Equal(68, offer.Destination.Port);
        Assert.True(offer.Packet.IsBroadcastFlagSet);
    }

    [Fact]
    public void No_broadcast_flag_means_unicast_to_hardware_address()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA))!;
        Assert.Equal(ReplyKind.UnicastToHardwareAddress, offer.Destination.Kind);
        Assert.Equal(offer.Packet.YourIp, offer.Destination.Address);
        Assert.Equal(MacA, offer.Destination.ClientMac);
    }

    [Fact]
    public void Always_broadcast_setting_overrides_unicast()
    {
        var offer = Engine(c => c with { AlwaysBroadcast = true }).Send(Client(DhcpMessageType.Discover, MacA))!;
        Assert.Equal(ReplyKind.Broadcast, offer.Destination.Kind);
    }

    [Fact]
    public void Client_that_ignores_unicast_offer_falls_back_to_broadcast()
    {
        var engine = Engine();
        Assert.Equal(ReplyKind.UnicastToHardwareAddress, engine.Send(Client(DhcpMessageType.Discover, MacA))!.Destination.Kind);
        _clock.Advance(TimeSpan.FromSeconds(4));
        var retry = engine.Send(Client(DhcpMessageType.Discover, MacA, xid: 2))!;
        Assert.Equal(ReplyKind.Broadcast, retry.Destination.Kind);
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.BroadcastFallback);

        var ack = engine.Send(Client(DhcpMessageType.Request, MacA, xid: 2, requested: retry.Packet.YourIp, serverId: Server))!;
        Assert.Equal(ReplyKind.Broadcast, ack.Destination.Kind);
    }

    [Fact]
    public void Renewal_with_ciaddr_is_unicast_to_ciaddr()
    {
        var engine = Engine();
        var ip = engine.Dora(MacA).Packet.YourIp;
        _clock.Advance(TimeSpan.FromMinutes(30));
        var ack = engine.Send(Client(DhcpMessageType.Request, MacA, xid: 5, ciaddr: ip))!;
        Assert.Equal(DhcpMessageType.Ack, ack.Packet.MessageType);
        Assert.Equal(ReplyKind.UnicastToClientIp, ack.Destination.Kind);
        Assert.Equal(ip, ack.Destination.Address);
        Assert.Equal(ip, ack.Packet.ClientIp);
        Assert.Equal(_clock.Now + TimeSpan.FromHours(1), engine.GetLeases().Single().Expires);
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.LeaseRenewed);
    }

    [Fact]
    public void Relayed_request_goes_back_to_relay_port_67()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA, giaddr: Ip("192.168.100.254")))!;
        Assert.Equal(ReplyKind.Relay, offer.Destination.Kind);
        Assert.Equal(Ip("192.168.100.254"), offer.Destination.Address);
        Assert.Equal(67, offer.Destination.Port);
        Assert.Equal(Ip("192.168.100.254"), offer.Packet.RelayIp);
    }

    // --- REQUEST states ---

    [Fact]
    public void Request_for_another_server_drops_our_offer_and_reports_it()
    {
        var engine = Engine(c => c with { PoolStart = Ip("192.168.100.10"), PoolEnd = Ip("192.168.100.10") });
        var offer = engine.Send(Client(DhcpMessageType.Discover, MacA))!;
        var reply = engine.Send(Client(DhcpMessageType.Request, MacA, requested: Ip("10.1.1.50"), serverId: Ip("10.1.1.1")));
        Assert.Null(reply);
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.OtherServerChosen && e.Message.Contains("10.1.1.1"));
        Assert.Equal(LeaseState.Expired, engine.GetLeases().Single().State);
        // The only address in the pool can now go to someone else.
        Assert.Equal(offer.Packet.YourIp, engine.Send(Client(DhcpMessageType.Discover, MacB))!.Packet.YourIp);
    }

    [Fact]
    public void Selecting_request_for_an_address_we_did_not_offer_is_nakked()
    {
        var engine = Engine();
        engine.Send(Client(DhcpMessageType.Discover, MacA));
        var nak = engine.Send(Client(DhcpMessageType.Request, MacA, requested: Ip("192.168.100.177"), serverId: Server))!;
        Assert.Equal(DhcpMessageType.Nak, nak.Packet.MessageType);
        Assert.Equal(ReplyKind.Broadcast, nak.Destination.Kind);
        Assert.True(IPv4.IsZero(nak.Packet.YourIp));
        Assert.Null(nak.Packet.GetOption(DhcpOptionCode.LeaseTime));
        Assert.NotNull(nak.Packet.GetOption(DhcpOptionCode.Message));
    }

    [Fact]
    public void Init_reboot_from_another_network_is_nakked()
    {
        var nak = Engine().Send(Client(DhcpMessageType.Request, MacA, requested: Ip("10.20.30.40")))!;
        Assert.Equal(DhcpMessageType.Nak, nak.Packet.MessageType);
    }

    [Fact]
    public void Init_reboot_for_free_pool_address_is_acked()
    {
        var engine = Engine();
        var ack = engine.Send(Client(DhcpMessageType.Request, MacA, requested: Ip("192.168.100.120")))!;
        Assert.Equal(DhcpMessageType.Ack, ack.Packet.MessageType);
        Assert.Equal(Ip("192.168.100.120"), ack.Packet.YourIp);
        Assert.Equal(LeaseState.Bound, engine.GetLeases().Single().State);
    }

    [Fact]
    public void Init_reboot_for_address_held_by_another_device_is_nakked()
    {
        var engine = Engine();
        var ipA = engine.Dora(MacA).Packet.YourIp;
        var nak = engine.Send(Client(DhcpMessageType.Request, MacB, requested: ipA))!;
        Assert.Equal(DhcpMessageType.Nak, nak.Packet.MessageType);
    }

    [Fact]
    public void Rebinding_for_unknown_but_free_address_is_adopted()
    {
        var engine = Engine();
        var ack = engine.Send(Client(DhcpMessageType.Request, MacA, ciaddr: Ip("192.168.100.130"), broadcast: true))!;
        Assert.Equal(DhcpMessageType.Ack, ack.Packet.MessageType);
        Assert.Equal(Ip("192.168.100.130"), ack.Packet.YourIp);
    }

    [Fact]
    public void Renewing_an_address_on_another_network_is_nakked()
    {
        var nak = Engine().Send(Client(DhcpMessageType.Request, MacA, ciaddr: Ip("172.16.0.9")))!;
        Assert.Equal(DhcpMessageType.Nak, nak.Packet.MessageType);
    }

    [Fact]
    public void Malformed_request_is_ignored()
    {
        Assert.Null(Engine().Send(Client(DhcpMessageType.Request, MacA)));
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.Ignored);
    }

    // --- DECLINE / RELEASE / INFORM ---

    [Fact]
    public void Decline_quarantines_address_and_explains_conflict()
    {
        var engine = Engine();
        var ip = engine.Dora(MacA).Packet.YourIp;
        Assert.Null(engine.Send(Client(DhcpMessageType.Decline, MacA, requested: ip, serverId: Server)));

        var evt = Assert.Single(_events, e => e.Kind == DhcpEventKind.LeaseDeclined);
        Assert.Contains("static IP", evt.Message);
        Assert.Contains(ip, engine.GetDeclinedAddresses());

        var next = engine.Send(Client(DhcpMessageType.Discover, MacA, xid: 7))!;
        Assert.NotEqual(ip, next.Packet.YourIp);

        _clock.Advance(TimeSpan.FromMinutes(11));
        engine.Tick();
        Assert.Empty(engine.GetDeclinedAddresses());
    }

    [Fact]
    public void Release_frees_address_but_new_devices_get_unused_addresses_first()
    {
        var engine = Engine();
        var ip = engine.Dora(MacA).Packet.YourIp;
        engine.Send(Client(DhcpMessageType.Release, MacA, ciaddr: ip, serverId: Server));
        Assert.Equal(LeaseState.Released, engine.GetLeases().Single().State);
        Assert.NotEqual(ip, engine.Dora(MacB).Packet.YourIp);
    }

    [Fact]
    public void Released_address_is_reused_when_pool_is_otherwise_full()
    {
        var engine = Engine(c => c with { PoolStart = Ip("192.168.100.10"), PoolEnd = Ip("192.168.100.10") });
        var ip = engine.Dora(MacA).Packet.YourIp;
        engine.Send(Client(DhcpMessageType.Release, MacA, ciaddr: ip, serverId: Server));
        Assert.Equal(ip, engine.Dora(MacB).Packet.YourIp);
    }

    [Fact]
    public void Inform_gets_ack_with_config_but_no_lease()
    {
        var reply = Engine().Send(Client(DhcpMessageType.Inform, MacA, ciaddr: Ip("192.168.100.50")))!;
        Assert.Equal(DhcpMessageType.Ack, reply.Packet.MessageType);
        Assert.True(IPv4.IsZero(reply.Packet.YourIp));
        Assert.Null(reply.Packet.GetOption(DhcpOptionCode.LeaseTime));
        Assert.NotNull(reply.Packet.GetOption(DhcpOptionCode.SubnetMask));
        Assert.Equal(ReplyKind.UnicastToClientIp, reply.Destination.Kind);
        Assert.Equal(Ip("192.168.100.50"), reply.Destination.Address);
    }

    // --- timers, BOOTP, misc ---

    [Fact]
    public void Unanswered_offer_expires_and_is_reported()
    {
        var engine = Engine();
        engine.Send(Client(DhcpMessageType.Discover, MacA));
        _clock.Advance(TimeSpan.FromSeconds(61));
        engine.Tick();
        Assert.Equal(LeaseState.Expired, engine.GetLeases().Single().State);
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.LeaseExpired && e.Message.Contains("never sent a REQUEST"));
    }

    [Fact]
    public void Bound_lease_expires_after_lease_time()
    {
        var engine = Engine();
        engine.Dora(MacA);
        _clock.Advance(TimeSpan.FromMinutes(61));
        engine.Tick();
        Assert.Equal(LeaseState.Expired, engine.GetLeases().Single().State);
    }

    [Fact]
    public void Plain_bootp_request_gets_bootp_reply()
    {
        var engine = Engine();
        var req = Client(DhcpMessageType.Discover, MacA);
        req.Options.Clear();
        req.HasMagicCookie = false;
        var reply = engine.Send(req)!;
        Assert.Null(reply.Packet.MessageType);
        Assert.False(reply.Packet.HasMagicCookie);
        Assert.Equal(Ip("192.168.100.100"), reply.Packet.YourIp);
        Assert.True(engine.GetLeases().Single().IsBootp);
    }

    [Fact]
    public void Bootreply_on_the_wire_is_ignored()
    {
        var p = Client(DhcpMessageType.Offer, MacA);
        p.Op = BootOp.Reply;
        Assert.Null(Engine().Send(p));
        Assert.Contains(_events, e => e.Kind == DhcpEventKind.Ignored && e.Message.Contains("another DHCP server"));
    }

    [Fact]
    public void Client_identifier_is_echoed()
    {
        var req = Client(DhcpMessageType.Discover, MacA);
        req.Options.Add(new DhcpOption(DhcpOptionCode.ClientId, [1, 0, 0x1D, 0xC1, 0x11, 0x22, 0x33]));
        var offer = Engine().Send(req)!;
        Assert.Equal(req.ClientId, offer.Packet.ClientId);
    }

    [Fact]
    public void Extra_options_are_sent_and_override_built_ins()
    {
        var engine = Engine(c => c with
        {
            ExtraOptions = new Dictionary<byte, byte[]>
            {
                [DhcpOptionCode.TftpServerAddress] = Ip("192.168.100.5").GetAddressBytes(),
                [DhcpOptionCode.Router] = Ip("192.168.100.253").GetAddressBytes(),
            },
        });
        var offer = engine.Send(Client(DhcpMessageType.Discover, MacA))!;
        Assert.Equal(Ip("192.168.100.5"), offer.Packet.GetOption(DhcpOptionCode.TftpServerAddress)!.AsAddress());
        Assert.Equal(Ip("192.168.100.253"), offer.Packet.GetOption(DhcpOptionCode.Router)!.AsAddress());
        Assert.Single(offer.Packet.Options, o => o.Code == DhcpOptionCode.Router);
    }

    [Fact]
    public void Message_type_is_first_option()
    {
        var offer = Engine().Send(Client(DhcpMessageType.Discover, MacA))!;
        Assert.Equal(DhcpOptionCode.MessageType, offer.Packet.Options[0].Code);
    }

    [Fact]
    public void Invalid_config_is_rejected_with_plain_english()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DhcpServerEngine(DhcpServerConfig.Default with { PoolStart = Ip("10.0.0.10"), PoolEnd = Ip("10.0.0.20") }));
        Assert.Contains("must be inside the server's subnet", ex.Message);
    }

    [Theory]
    [InlineData("192.168.100.0", 24, "network or broadcast")]
    [InlineData("169.254.1.1", 16, "reserved range")]
    public void Config_validation_catches_bad_server_ip(string server, int prefix, string expected)
    {
        var c = DhcpServerConfig.Default with { ServerIp = Ip(server), PrefixLength = prefix };
        Assert.Contains(c.Validate(), e => e.Contains(expected));
    }

    [Fact]
    public void Default_config_is_valid()
    {
        Assert.Empty(DhcpServerConfig.Default.Validate());
        Assert.Equal(100, DhcpServerConfig.Default.PoolSize);
    }
}
