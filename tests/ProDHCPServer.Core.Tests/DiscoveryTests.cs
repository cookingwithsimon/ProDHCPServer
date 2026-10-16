using System.Buffers.Binary;
using System.Net;
using System.Text;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Discovery;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Tests;

public class DiscoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly MacAddress Device = MacAddress.Parse("00:05:A6:0D:12:34");
    private static readonly MacAddress Switch = MacAddress.Parse("00:1B:2C:3D:4E:5F");

    // ---------- packet builders ----------

    private static byte[] Ethernet(MacAddress dst, MacAddress src, ushort type, byte[] payload) =>
        [.. dst.ToBytes(), .. src.ToBytes(), (byte)(type >> 8), (byte)type, .. payload];

    private static byte[] Udp(string src, string dst, int sport, int dport, byte[] payload)
    {
        var p = new byte[28 + payload.Length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[8] = 64;
        p[9] = 17;
        IPv4.Parse(src).GetAddressBytes().CopyTo(p, 12);
        IPv4.Parse(dst).GetAddressBytes().CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), (ushort)dport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), (ushort)(8 + payload.Length));
        payload.CopyTo(p, 28);
        return p;
    }

    private static byte[] Tlv(int type, params byte[] value) => [(byte)((type << 1) | (value.Length >> 8)), (byte)value.Length, .. value];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Name(string name) => [.. name.Split('.').SelectMany(l => (byte[])[(byte)l.Length, .. Ascii(l)]), 0];

    private static byte[] Record(string name, DnsType type, byte[] rdata, uint ttl = 120)
    {
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 0x8001);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)rdata.Length);
        return [.. Name(name), .. fixedPart, .. rdata];
    }

    private static byte[] MdnsResponse(params byte[][] records) =>
        [0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0, .. records.SelectMany(r => r)];

    private static byte[] SscAnnouncement() => MdnsResponse(
        Record("_ssc._udp.local", DnsType.Ptr, Name("EW-DX EM 2._ssc._udp.local")),
        Record("EW-DX EM 2._ssc._udp.local", DnsType.Srv, [0, 0, 0, 0, 0, 45, .. Name("ewdx-em2.local")]),
        Record("EW-DX EM 2._ssc._udp.local", DnsType.Txt, [9, .. Ascii("txtvers=1"), 11, .. Ascii("model=EW-DX")]),
        Record("ewdx-em2.local", DnsType.A, [192, 168, 0, 40]));

    // ---------- IP, Ethernet, ARP ----------

    [Fact]
    public void Udp_inside_an_ip_packet_is_found()
    {
        var packet = Udp("192.168.1.50", "255.255.255.255", 5000, 6000, [1, 2, 3]);
        Assert.True(IPv4Packet.TryParse(packet, out var ip));
        Assert.Equal(IPv4.Parse("192.168.1.50"), ip.Source);
        Assert.True(ip.TryGetUdp(out int sp, out int dp, out var payload));
        Assert.Equal((5000, 6000), (sp, dp));
        Assert.Equal([1, 2, 3], payload.ToArray());
    }

    [Fact]
    public void Any_broadcast_from_another_subnet_counts_as_a_sighting()
    {
        var parsed = DiscoveryParser.ParseIp(T0, Udp("192.168.254.254", "255.255.255.255", 4000, 4000, [0]));
        var s = Assert.Single(parsed.Sightings);
        Assert.Equal(DiscoverySource.Traffic, s.Source);
        Assert.Equal(IPv4.Parse("192.168.254.254"), s.Ip);
    }

    [Fact]
    public void Packets_from_no_address_are_ignored()
    {
        Assert.Empty(DiscoveryParser.ParseIp(T0, Udp("0.0.0.0", "255.255.255.255", 68, 67, [0])).Sightings);
    }

    [Fact]
    public void An_arp_probe_gives_the_address_a_device_is_about_to_use()
    {
        byte[] arp = [0, 1, 8, 0, 6, 4, 0, 1, .. Device.ToBytes(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 192, 168, 254, 254];
        var (s, port) = DiscoveryParser.ParseFrame(T0, Ethernet(MacAddress.Parse("FF:FF:FF:FF:FF:FF"), Device, EthernetFrame.Arp, arp));
        Assert.Null(port);
        Assert.NotNull(s);
        Assert.Equal(DiscoverySource.ArpProbe, s.Source);
        Assert.Equal(IPv4.Parse("192.168.254.254"), s.Ip);
        Assert.Equal(Device, s.Mac);
        Assert.Contains("power-up", s.Detail);
    }

    [Fact]
    public void A_gratuitous_arp_is_an_announcement()
    {
        byte[] arp = [0, 1, 8, 0, 6, 4, 0, 1, .. Device.ToBytes(), 192, 168, 1, 39, 0, 0, 0, 0, 0, 0, 192, 168, 1, 39];
        var packet = ArpPacket.TryParse(arp)!;
        Assert.True(packet.IsAnnouncement);
        Assert.False(packet.IsProbe);
        Assert.Equal(IPv4.Parse("192.168.1.39"), packet.ClaimedIp);
    }

    // ---------- LLDP and CDP ----------

    [Fact]
    public void Lldp_names_the_switch_port_and_vlan()
    {
        byte[] lldp =
        [
            .. Tlv(1, [4, .. Switch.ToBytes()]),
            .. Tlv(2, [5, .. Ascii("Gi1/0/5")]),
            .. Tlv(3, 0, 120),
            .. Tlv(4, Ascii("Rack 3")),
            .. Tlv(5, Ascii("core-sw1")),
            .. Tlv(8, [5, 1, 10, 0, 0, 2, 2, 0, 0, 0, 1, 0]),
            .. Tlv(127, 0x00, 0x80, 0xC2, 1, 0, 20),
            .. Tlv(0),
        ];
        var (_, port) = DiscoveryParser.ParseFrame(T0, Ethernet(Lldp.Multicast, Switch, EthernetFrame.Lldp, lldp));
        Assert.NotNull(port);
        Assert.Equal("Plugged into core-sw1 port Gi1/0/5 (Rack 3), VLAN 20", port.Describe());
        Assert.Equal(IPv4.Parse("10.0.0.2"), port.ManagementIp);
        Assert.Equal(TimeSpan.FromSeconds(120), port.TimeToLive);
    }

    [Fact]
    public void Lldp_without_a_system_name_falls_back_to_the_chassis()
    {
        byte[] lldp = [.. Tlv(1, [4, .. Switch.ToBytes()]), .. Tlv(2, [5, .. Ascii("port 7")]), .. Tlv(3, 0, 120), .. Tlv(0)];
        var port = Lldp.TryParse(lldp, Switch)!;
        Assert.Equal($"Plugged into {Switch} port port 7", port.Describe());
    }

    [Fact]
    public void Cdp_names_the_switch_port_and_native_vlan()
    {
        static byte[] T(int type, byte[] v) => [(byte)(type >> 8), (byte)type, (byte)((v.Length + 4) >> 8), (byte)(v.Length + 4), .. v];
        byte[] cdp =
        [
            2, 180, 0, 0,
            .. T(0x0001, Ascii("sw2")),
            .. T(0x0002, [0, 0, 0, 1, 1, 1, 0xCC, 0, 4, 192, 168, 1, 2]),
            .. T(0x0003, Ascii("GigabitEthernet0/3")),
            .. T(0x0006, Ascii("cisco WS-C2960")),
            .. T(0x000A, [0, 10]),
        ];
        byte[] snap = [0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00, .. cdp];
        byte[] frame = [.. Cdp.Multicast.ToBytes(), .. Switch.ToBytes(), (byte)(snap.Length >> 8), (byte)snap.Length, .. snap];
        var (_, port) = DiscoveryParser.ParseFrame(T0, frame);
        Assert.NotNull(port);
        Assert.Equal("CDP", port.Protocol);
        Assert.Equal("Plugged into sw2 port GigabitEthernet0/3, VLAN 10", port.Describe());
        Assert.Equal("cisco WS-C2960", port.Model);
        Assert.Equal(IPv4.Parse("192.168.1.2"), port.ManagementIp);
    }

    // ---------- mDNS ----------

    [Fact]
    public void Mdns_query_asks_for_each_name()
    {
        var q = DnsMessage.BuildQuery(["_ssc._udp.local", "_http._tcp.local"]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));
        Assert.Null(DnsMessage.TryParseResponse(q)); // a query, not a response
        Assert.Equal(Name("_ssc._udp.local"), q[12..(12 + Name("_ssc._udp.local").Length)]);
    }

    [Fact]
    public void Mdns_response_gives_the_device_name_model_and_services()
    {
        var parsed = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.0.40"), 5353, 5353, SscAnnouncement());
        var s = Assert.Single(parsed.Sightings);
        Assert.Equal(DiscoverySource.Mdns, s.Source);
        Assert.Equal("EW-DX EM 2", s.Name);
        Assert.Equal("EW-DX", s.Model);
        Assert.Equal("Sennheiser SSC", s.Label);
        Assert.Equal("mDNS services: _ssc._udp (port 45)", s.Detail);
    }

    [Fact]
    public void Mdns_service_list_is_passed_on_to_ask_about_next()
    {
        var response = MdnsResponse(
            Record("_services._dns-sd._udp.local", DnsType.Ptr, Name("_qsys-thing._tcp.local")),
            Record("_services._dns-sd._udp.local", DnsType.Ptr, Name("_http._tcp.local")));
        var parsed = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.60"), 5353, 5353, response);
        Assert.Equal(["_qsys-thing._tcp", "_http._tcp"], parsed.MdnsTypes);
    }

    [Fact]
    public void Mdns_http_service_becomes_the_web_page()
    {
        var response = MdnsResponse(Record("Mixer._http._tcp.local", DnsType.Srv, [0, 0, 0, 0, 0x1F, 0x90, .. Name("mixer.local")]));
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.61"), 5353, 5353, response).Sightings[0];
        Assert.Equal(new Uri("http://192.168.1.61:8080/"), s.WebPage);
    }

    [Fact]
    public void Compressed_names_are_followed()
    {
        // "a.local" at offset 0, then "b" + pointer to "local" (offset 2).
        byte[] data = [1, (byte)'a', 5, .. Ascii("local"), 0, 1, (byte)'b', 0xC0, 2];
        int offset = 9;
        Assert.Equal("b.local", DnsMessage.ReadName(data, ref offset));
        Assert.Equal(13, offset);
    }

    [Fact]
    public void A_pointer_loop_is_rejected()
    {
        Assert.Null(DnsMessage.TryParseResponse(MdnsResponse([0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0])));
    }

    [Fact]
    public void Garbage_on_the_mdns_port_is_just_traffic()
    {
        var s = Assert.Single(DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.62"), 5353, 5353, [1, 2, 3]).Sightings);
        Assert.Equal(DiscoverySource.Traffic, s.Source);
    }

    // ---------- SSDP / UPnP ----------

    [Fact]
    public void Ssdp_response_is_read_and_its_description_queued()
    {
        var text = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.1.70:49152/desc.xml\r\nSERVER: Linux UPnP/1.0 Barco/1\r\nST: upnp:rootdevice\r\nUSN: uuid:1234\r\n\r\n";
        var parsed = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.70"), 1900, 50000, Ascii(text));
        Assert.Equal(new Uri("http://192.168.1.70:49152/desc.xml"), parsed.DescriptionToFetch);
        Assert.Equal("UPnP: Linux UPnP/1.0 Barco/1, upnp:rootdevice", parsed.Sightings[0].Detail);
    }

    [Theory]
    [InlineData("http://10.0.0.5/desc.xml")]       // a different host
    [InlineData("https://192.168.1.70/desc.xml")]  // not plain http
    [InlineData("file:///c:/windows/win.ini")]
    public void Ssdp_location_must_be_http_on_the_sender(string location)
    {
        var msg = Ssdp.TryParse(Ascii($"NOTIFY * HTTP/1.1\r\nLOCATION: {location}\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\n\r\n"))!;
        Assert.True(msg.IsNotify);
        Assert.Null(Ssdp.SafeLocation(msg, IPv4.Parse("192.168.1.70")));
    }

    [Fact]
    public void Upnp_description_gives_name_model_and_web_page()
    {
        var xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <friendlyName>Boardroom ClickShare</friendlyName><manufacturer>Barco</manufacturer>
              <modelName>CX-50</modelName><modelNumber>R9861520</modelNumber><serialNumber>1863012345</serialNumber>
              <presentationURL>/index.html</presentationURL>
            </device></root>
            """;
        var d = UpnpDescription.TryParse(xml)!;
        Assert.Equal("CX-50 R9861520", d.Model);
        var location = new Uri("http://192.168.1.70:49152/desc.xml");
        var s = DiscoveryParser.FromDescription(T0, IPv4.Parse("192.168.1.70"), d, location);
        Assert.Equal("Boardroom ClickShare", s.Name);
        Assert.Equal("Barco", s.Manufacturer);
        Assert.Equal(new Uri("http://192.168.1.70:49152/index.html"), s.WebPage);
    }

    [Fact]
    public void Upnp_description_with_a_dtd_is_refused()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x \"y\">]><root><device><friendlyName>&x;</friendlyName></device></root>";
        Assert.Null(UpnpDescription.TryParse(xml));
    }

    // ---------- vendor protocols ----------

    [Fact]
    public void Pjlink_search_reply_gives_the_mac()
    {
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.0.10"), 4352, 4352, Ascii("%2ACKN=00:0b:a2:11:22:33\r")).Sightings[0];
        Assert.Equal(DiscoverySource.Vendor, s.Source);
        Assert.Equal("PJLink", s.Label);
        Assert.Equal(MacAddress.Parse("00:0B:A2:11:22:33"), s.Mac);
    }

    [Fact]
    public void Pjlink_queries_go_to_each_subnet_broadcast_and_everywhere()
    {
        var queries = VendorProtocols.Queries([new AddressAssignment(IPv4.Parse("192.168.100.1"), 24), new AddressAssignment(IPv4.Parse("192.168.1.249"), 24)]);
        var pj = queries.Where(q => q.Name == "PJLink").Select(q => q.Destination.ToString()).ToList();
        Assert.Equal(["192.168.100.255", "192.168.1.255", "255.255.255.255"], pj);
        Assert.Equal("%2SRCH\r", Encoding.ASCII.GetString(queries[0].Payload));
        // Art-Net's spec forbids the limited broadcast.
        Assert.DoesNotContain(queries, q => q.Name == "Art-Net" && q.Destination.Equals(IPAddress.Broadcast));
        Assert.All(queries.Where(q => q.Name == "Art-Net"), q => Assert.Equal(0, q.Payload[12])); // flags 0: no unsolicited replies
    }

    [Fact]
    public void Art_net_reply_gives_name_and_mac()
    {
        var p = new byte[239];
        Ascii("Art-Net\0").CopyTo(p, 0);
        p[8] = 0x00; p[9] = 0x21;
        p[16] = 1; p[17] = 4;
        Ascii("Node 1").CopyTo(p, 26);
        Ascii("Stage Left DMX").CopyTo(p, 44);
        Device.ToBytes().CopyTo(p, 201);
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("2.0.0.10"), 6454, 6454, p).Sightings[0];
        Assert.Equal("Art-Net", s.Label);
        Assert.Equal("Stage Left DMX", s.Name);
        Assert.Equal(Device, s.Mac);
        Assert.Contains("firmware 1.4", s.Detail);
    }

    [Fact]
    public void Art_net_poll_from_another_controller_is_just_traffic()
    {
        byte[] poll = [.. Ascii("Art-Net\0"), 0x00, 0x20, 0, 14, 0, 0];
        Assert.Equal(DiscoverySource.Traffic, DiscoveryParser.ParseUdp(T0, IPv4.Parse("2.0.0.11"), 6454, 6454, poll).Sightings[0].Source);
    }

    [Fact]
    public void Sap_announcement_names_the_stream_and_its_sender()
    {
        byte[] sap = [0x20, 0, 0x12, 0x34, 192, 168, 1, 60, .. Ascii("application/sdp\0v=0\r\no=- 1 1 IN IP4 192.168.1.60\r\ns=Stage Mix\r\nc=IN IP4 239.69.1.1/32\r\n")];
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.60"), 9875, 9875, sap).Sightings[0];
        Assert.Equal("AES67", s.Label);
        Assert.Equal(IPv4.Parse("192.168.1.60"), s.Ip);
        Assert.Equal("Announces an AES67 stream \"Stage Mix\" (SAP)", s.Detail);
    }

    [Fact]
    public void Sap_deletion_is_ignored()
    {
        byte[] sap = [0x24, 0, 0, 0, 192, 168, 1, 60, .. Ascii("v=0\r\ns=Gone\r\n")];
        Assert.Equal(DiscoverySource.Traffic, DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.60"), 9875, 9875, sap).Sightings[0].Source);
    }

    [Fact]
    public void Amx_beacon_gives_make_model_mac_and_web_page()
    {
        var text = "AMXB<-UUID=0005A60D1234><-SDKClass=VideoProjector><-Make=Optoma><-Model=EH501><-Revision=1.0.0><Config-URL=http://192.168.1.80/>";
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.80"), 9131, 9131, Ascii(text)).Sightings[0];
        Assert.Equal("AMX beacon", s.Label);
        Assert.Equal("Optoma", s.Manufacturer);
        Assert.Equal("EH501", s.Model);
        Assert.Equal(Device, s.Mac);
        Assert.Equal(new Uri("http://192.168.1.80/"), s.WebPage);
        Assert.Equal("AMX Device Discovery beacon (VideoProjector), firmware 1.0.0", s.Detail);
    }

    [Fact]
    public void Amx_beacon_web_page_must_be_on_the_sender()
    {
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.80"), 9131, 9131, Ascii("AMXB<-Make=X><Config-URL=http://evil.example/>")).Sightings[0];
        Assert.Null(s.WebPage);
    }

    [Fact]
    public void Qsys_announcement_is_labelled_and_read_best_effort()
    {
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.90"), 2467, 2467,
            Ascii("<QDP><device><name>Core-110f-Rack1</name><type>core</type><part_number>Core 110f</part_number></device></QDP>")).Sightings[0];
        Assert.Equal("Q-SYS", s.Label);
        Assert.Equal("Core-110f-Rack1", s.Name);
        Assert.Equal("Core 110f", s.Model);
    }

    [Fact]
    public void Amx_netlinx_broadcast_marks_a_controller()
    {
        var s = DiscoveryParser.ParseUdp(T0, IPv4.Parse("192.168.1.91"), 1319, 1319, [0, 1, 2]).Sightings[0];
        Assert.Equal("AMX NetLinx", s.Label);
    }

    // ---------- the device table ----------

    private static Sighting See(string ip, DiscoverySource src, MacAddress? mac = null, string? name = null, int second = 0) =>
        new(T0.AddSeconds(second), src, IPv4.Parse(ip)) { Mac = mac, Name = name };

    [Fact]
    public void An_ip_only_sighting_merges_into_the_device_once_its_mac_is_known()
    {
        var t = new DiscoveryTable();
        Assert.Equal(DiscoveryChange.New, t.Add(See("192.168.1.50", DiscoverySource.Traffic))!.Change);
        Assert.Equal(DiscoveryChange.MoreDetail, t.Add(See("192.168.1.50", DiscoverySource.Sweep, Device))!.Change);
        Assert.Null(t.Add(See("192.168.1.50", DiscoverySource.Traffic, second: 5)));
        var d = Assert.Single(t.Snapshot());
        Assert.Equal(Device, d.Mac);
        Assert.Equal(["its traffic", "subnet scan"], d.FoundBy);
        Assert.Equal(T0.AddSeconds(5), d.LastSeen);
    }

    [Fact]
    public void A_device_that_changes_address_is_followed_by_mac()
    {
        var t = new DiscoveryTable();
        t.Add(See("192.168.254.254", DiscoverySource.ArpProbe, Device));
        var u = t.Add(See("192.168.100.100", DiscoverySource.ArpTable, Device))!;
        Assert.Equal(DiscoveryChange.IpChanged, u.Change);
        Assert.Equal(IPv4.Parse("192.168.254.254"), u.PreviousIp);
        Assert.Single(t.Snapshot());
    }

    [Fact]
    public void Two_macs_on_one_address_are_two_devices()
    {
        var t = new DiscoveryTable();
        t.Add(See("192.168.1.39", DiscoverySource.Arp, Device));
        t.Add(See("192.168.1.39", DiscoverySource.Arp, Switch));
        Assert.Equal(2, t.Snapshot().Count);
    }

    [Theory]
    [InlineData("192.168.100.1")]   // this laptop
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.251")]
    [InlineData("127.0.0.1")]
    public void Own_and_special_addresses_are_never_listed(string ip)
    {
        var t = new DiscoveryTable();
        t.SetOwnAddresses([IPv4.Parse("192.168.100.1")]);
        Assert.Null(t.Add(See(ip, DiscoverySource.Traffic)));
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void The_first_name_a_device_gives_is_kept()
    {
        var t = new DiscoveryTable();
        t.Add(See("192.168.1.50", DiscoverySource.Mdns, name: "Stage Box"));
        t.Add(See("192.168.1.50", DiscoverySource.Ssdp, name: "Something else"));
        Assert.Equal("Stage Box", t.Snapshot()[0].Title);
    }

    // ---------- subnet planning ----------

    [Fact]
    public void Helper_addresses_start_high_and_skip_factory_and_known_addresses()
    {
        var avoid = new HashSet<uint> { IPv4.ToUInt32(IPv4.Parse("192.168.254.249")) };
        var first = SubnetPlanner.HelperCandidates(IPv4.Parse("192.168.254.254"), avoid).Take(2).Select(a => a.ToString()).ToList();
        // .250 is Extron's documented factory address and .249 is taken.
        Assert.Equal(["192.168.254.248", "192.168.254.247"], first);
    }

    [Fact]
    public void Self_assigned_devices_get_a_helper_in_169_254()
    {
        Assert.Equal(16, SubnetPlanner.ReachPrefix(IPv4.Parse("169.254.12.7")));
        Assert.Equal("169.254.12.249", SubnetPlanner.HelperCandidates(IPv4.Parse("169.254.12.7"), new HashSet<uint>()).First().ToString());
        // 169.254.0.x is reserved, so the helper moves to 169.254.1.x.
        Assert.Equal("169.254.1.249", SubnetPlanner.HelperCandidates(IPv4.Parse("169.254.0.7"), new HashSet<uint>()).First().ToString());
    }

    [Fact]
    public void Sweep_covers_own_seen_and_preset_subnets_but_not_wifi_ones()
    {
        var local = new[] { new AddressAssignment(IPv4.Parse("192.168.100.1"), 24) };
        var seen = new[] { new DiscoveredDevice { Id = 1, Ip = IPv4.Parse("10.20.30.40") } };
        var wifi = new AdapterInfo { Id = "wifi", Index = 9, Name = "Wi-Fi", MediaConnected = true, Addresses = [new(IPv4.Parse("192.168.1.23"), 24)] };

        var (targets, skipped) = SubnetPlanner.SweepTargets(local, seen, [wifi]);

        Assert.Equal(["192.168.100.x", "10.20.30.x", "192.168.254.x", "192.168.0.x"], targets.Select(t => t.Short));
        var skip = Assert.Single(skipped);
        Assert.Equal("192.168.1.x", skip.Subnet);
        Assert.Contains("Wi-Fi", skip.Reason);
    }

    // ---------- temporary addresses: reach and sweep ----------

    private sealed class FakeHost : IHelperAddressHost
    {
        public List<AddressAssignment> Addresses { get; } = [new(IPv4.Parse("192.168.100.1"), 24)];
        public List<string> Calls { get; } = [];
        public HashSet<string> Duplicates { get; } = [];
        public bool NeverReady { get; set; }

        public IReadOnlyList<AddressAssignment> CurrentAddresses() => Addresses.ToList();
        public void Add(IPAddress address, int prefixLength) { Calls.Add($"add {address}/{prefixLength}"); if (!NeverReady && !Duplicates.Contains(address.ToString())) Addresses.Add(new(address, prefixLength)); }
        public void Remove(IPAddress address) { Calls.Add($"remove {address}"); Addresses.RemoveAll(a => a.Address.Equals(address)); }
        public HelperState State(IPAddress address) =>
            Duplicates.Contains(address.ToString()) ? HelperState.Duplicate
            : Addresses.Any(a => a.Address.Equals(address)) ? HelperState.Ready : HelperState.Pending;
    }

    private sealed class FakeProber(params string[] answering) : IArpProber
    {
        public List<(string Target, string Source)> Probes { get; } = [];
        public Task<MacAddress?> ProbeAsync(IPAddress target, IPAddress source, CancellationToken ct)
        {
            lock (Probes) Probes.Add((target.ToString(), source.ToString()));
            return Task.FromResult<MacAddress?>(answering.Contains(target.ToString()) ? Device : null);
        }
    }

    [Fact]
    public async Task Reach_adds_a_free_address_on_the_device_subnet()
    {
        var host = new FakeHost();
        host.Duplicates.Add("192.168.1.249");
        var helper = new SubnetHelper(host, new FakeProber()) { PollInterval = TimeSpan.Zero };

        var result = await helper.ReachAsync(IPv4.Parse("192.168.1.39"), new HashSet<uint>(), CancellationToken.None);

        Assert.Equal(IPv4.Parse("192.168.1.248"), result.Address);
        Assert.Equal(["add 192.168.1.249/24", "remove 192.168.1.249", "add 192.168.1.248/24"], host.Calls);
    }

    [Fact]
    public async Task Reach_does_nothing_when_already_on_the_subnet()
    {
        var host = new FakeHost();
        var result = await new SubnetHelper(host, new FakeProber()).ReachAsync(IPv4.Parse("192.168.100.50"), new HashSet<uint>(), CancellationToken.None);
        Assert.True(result.AlreadyReachable);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Reach_gives_up_and_cleans_up_if_windows_never_brings_the_address_up()
    {
        var host = new FakeHost { NeverReady = true };
        var clock = T0;
        var helper = new SubnetHelper(host, new FakeProber(), () => clock += TimeSpan.FromSeconds(1)) { PollInterval = TimeSpan.Zero };

        var result = await helper.ReachAsync(IPv4.Parse("192.168.1.39"), new HashSet<uint>(), CancellationToken.None);

        Assert.Null(result.Address);
        Assert.Contains("is the cable connected", result.Problem);
        Assert.Equal(["add 192.168.1.249/24", "remove 192.168.1.249"], host.Calls);
    }

    [Fact]
    public async Task Sweep_uses_a_temporary_address_and_removes_it_afterwards()
    {
        var host = new FakeHost();
        var prober = new FakeProber("192.168.254.254");
        var found = new List<Sighting>();
        var helper = new SubnetHelper(host, prober) { PollInterval = TimeSpan.Zero };

        var summary = await helper.SweepAsync([new SweepTarget(IPv4.Parse("192.168.254.0"), 24, "test")], new HashSet<uint>(), _ => { }, found.Add, CancellationToken.None);

        Assert.Equal(1, summary.Found);
        var s = Assert.Single(found);
        Assert.Equal(IPv4.Parse("192.168.254.254"), s.Ip);
        Assert.Equal(DiscoverySource.Sweep, s.Source);
        Assert.Equal(["add 192.168.254.249/24", "remove 192.168.254.249"], host.Calls);
        Assert.Equal(253, prober.Probes.Count); // every address but the helper's own
        Assert.All(prober.Probes, p => Assert.Equal("192.168.254.249", p.Source));
        Assert.Single(host.Addresses);
    }

    [Fact]
    public async Task Sweep_of_the_laptops_own_subnet_needs_no_temporary_address()
    {
        var host = new FakeHost();
        var prober = new FakeProber("192.168.100.50");
        var found = new List<Sighting>();
        await new SubnetHelper(host, prober).SweepAsync([new SweepTarget(IPv4.Parse("192.168.100.0"), 24, "own")], new HashSet<uint>(), _ => { }, found.Add, CancellationToken.None);
        Assert.Empty(host.Calls);
        Assert.Single(found);
        Assert.DoesNotContain(prober.Probes, p => p.Target == "192.168.100.1");
    }

    [Fact]
    public async Task A_cancelled_sweep_still_removes_its_address()
    {
        var host = new FakeHost();
        using var cts = new CancellationTokenSource();
        var prober = new CancellingProber(cts);
        var helper = new SubnetHelper(host, prober) { PollInterval = TimeSpan.Zero };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            helper.SweepAsync([new SweepTarget(IPv4.Parse("192.168.0.0"), 24, "test")], new HashSet<uint>(), _ => { }, _ => { }, cts.Token));

        Assert.Equal("remove 192.168.0.249", host.Calls[^1]);
        Assert.Single(host.Addresses);
    }

    private sealed class CancellingProber(CancellationTokenSource cts) : IArpProber
    {
        public Task<MacAddress?> ProbeAsync(IPAddress target, IPAddress source, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult<MacAddress?>(null);
        }
    }
}
