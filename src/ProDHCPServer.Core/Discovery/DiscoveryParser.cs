using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Discovery;

/// <summary>What one received packet told us.</summary>
public sealed record ParsedPacket(IReadOnlyList<Sighting> Sightings, Uri? DescriptionToFetch = null)
{
    public static readonly ParsedPacket Nothing = new([]);

    /// <summary>mDNS service types a device advertised, to ask about next.</summary>
    public IReadOnlyList<string> MdnsTypes { get; init; } = [];
}

/// <summary>Turns received packets into sightings. Pure, so every protocol is unit-tested.</summary>
public static class DiscoveryParser
{
    /// <summary>
    /// A UDP payload from <paramref name="source"/>. mDNS, SSDP and the vendor protocols are decoded; anything
    /// else still counts as the device being there ("its traffic").
    /// </summary>
    public static ParsedPacket ParseUdp(DateTimeOffset at, IPAddress source, int sourcePort, int destinationPort, ReadOnlySpan<byte> payload)
    {
        if (!DiscoveryTable.IsDeviceAddress(source)) return ParsedPacket.Nothing;

        if (sourcePort == DnsMessage.MdnsPort || destinationPort == DnsMessage.MdnsPort)
        {
            if (DnsMessage.TryParseResponse(payload) is { Count: > 0 } records)
            {
                var announcement = MdnsInterpreter.Interpret(records);
                return new([FromMdns(at, source, announcement)]) { MdnsTypes = announcement.AdvertisedTypes };
            }
            return new([Traffic(at, source, "Sent an mDNS query")]);
        }

        if (sourcePort == Ssdp.Port || destinationPort == Ssdp.Port)
        {
            if (Ssdp.TryParse(payload) is { IsByeBye: false } ssdp)
            {
                var detail = string.Join(", ", new[] { ssdp.Server, ssdp.Type }.Where(v => !string.IsNullOrEmpty(v)));
                return new([new Sighting(at, DiscoverySource.Ssdp, source) { Detail = detail.Length > 0 ? $"UPnP: {detail}" : null }],
                    Ssdp.SafeLocation(ssdp, source));
            }
            return new([Traffic(at, source, "Sent a UPnP search")]);
        }

        if (VendorProtocols.TryParse(at, source, sourcePort, destinationPort, payload) is { } vendor) return new([vendor]);
        return new([Traffic(at, source, $"Sent UDP to port {destinationPort}")]);
    }

    /// <summary>A non-UDP IPv4 packet (IGMP group joins, pings): the device is there, nothing more.</summary>
    public static ParsedPacket ParseOther(DateTimeOffset at, IPAddress source, byte protocol) =>
        DiscoveryTable.IsDeviceAddress(source)
            ? new([Traffic(at, source, protocol == IPv4Packet.Igmp ? "Joined a multicast group (IGMP)" : null)])
            : ParsedPacket.Nothing;

    /// <summary>A whole IPv4 packet from a raw socket.</summary>
    public static ParsedPacket ParseIp(DateTimeOffset at, ReadOnlySpan<byte> packet)
    {
        if (!IPv4Packet.TryParse(packet, out var ip)) return ParsedPacket.Nothing;
        return ip.TryGetUdp(out int sp, out int dp, out var payload)
            ? ParseUdp(at, ip.Source, sp, dp, payload)
            : ip.Protocol == IPv4Packet.Udp ? ParsedPacket.Nothing : ParseOther(at, ip.Source, ip.Protocol);
    }

    /// <summary>
    /// An Ethernet frame captured with Npcap: ARP gives a device's MAC and IP (even on another subnet),
    /// LLDP and CDP describe the switch port.
    /// </summary>
    public static (Sighting? Sighting, SwitchPortInfo? SwitchPort) ParseFrame(DateTimeOffset at, ReadOnlySpan<byte> data)
    {
        if (!EthernetFrame.TryParse(data, out var frame)) return (null, null);
        if (frame.EtherType == EthernetFrame.Arp && ArpPacket.TryParse(frame.Payload) is { } arp)
        {
            var ip = arp.ClaimedIp;
            if (!DiscoveryTable.IsDeviceAddress(ip)) return (null, null);
            string detail = arp.IsProbe ? $"Checked {ip} was free (ARP probe), as devices do at power-up"
                : arp.IsAnnouncement ? $"Announced {ip} (gratuitous ARP)"
                : arp.Operation == ArpPacket.Reply ? $"Answered ARP for {ip}" : $"Asked who has {arp.TargetIp} (ARP)";
            return (new Sighting(at, arp.IsProbe ? DiscoverySource.ArpProbe : DiscoverySource.Arp, ip) { Mac = arp.SenderMac, Detail = detail }, null);
        }
        if (frame.EtherType == EthernetFrame.Lldp) return (null, Lldp.TryParse(frame.Payload, frame.Source));
        if (frame.IsCdp) return (null, Cdp.TryParse(frame.Payload, frame.Source));
        return (null, null);
    }

    /// <summary>The device description fetched from an SSDP LOCATION.</summary>
    public static Sighting FromDescription(DateTimeOffset at, IPAddress source, UpnpDescription d, Uri location) =>
        new(at, DiscoverySource.Ssdp, source)
        {
            Name = d.FriendlyName,
            Model = d.Model,
            Manufacturer = d.Manufacturer,
            WebPage = d.WebPage(location),
            Detail = d.SerialNumber is { } serial ? $"UPnP description: {d.Model}, serial {serial}" : $"UPnP description at {location}",
        };

    private static Sighting FromMdns(DateTimeOffset at, IPAddress source, MdnsAnnouncement a)
    {
        var label = a.Services.Select(s => MdnsCatalog.Label(s.Type)).FirstOrDefault(l => l is not null);
        var instance = a.Services.Select(s => s.Instance).FirstOrDefault(i => i.Length > 0 && !i.StartsWith('_'));
        var types = a.Services.Select(s => s.Port > 0 ? $"{s.Type} (port {s.Port})" : s.Type).Distinct().Take(6).ToList();
        var web = a.Services.FirstOrDefault(s => s.Type is "_http._tcp" or "_https._tcp" && s.Port > 0);
        return new Sighting(at, DiscoverySource.Mdns, source)
        {
            Name = instance ?? a.HostName,
            Model = MdnsInterpreter.Model(a.Properties),
            Manufacturer = MdnsInterpreter.Manufacturer(a.Properties),
            Label = label ?? "mDNS",
            WebPage = web is null ? null : new Uri($"{(web.Type == "_https._tcp" ? "https" : "http")}://{source}{(web.Port is 80 or 443 ? "" : $":{web.Port}")}/"),
            Detail = types.Count > 0 ? $"mDNS services: {string.Join(", ", types)}" : a.HostName is { } h ? $"mDNS name {h}.local" : null,
        };
    }

    private static Sighting Traffic(DateTimeOffset at, IPAddress source, string? detail) =>
        new(at, DiscoverySource.Traffic, source) { Detail = detail };

    /// <summary>For log lines: "192.168.1.50 (Extron IN1606, AA:BB:..)".</summary>
    public static string Describe(DiscoveredDevice d)
    {
        var parts = new List<string>();
        if (d.Name is not null) parts.Add(d.Name);
        if (d.Model is not null && d.Model != d.Name) parts.Add(d.Model);
        var maker = d.Manufacturer ?? (d.Mac is { } mac ? OuiDatabase.Default.Lookup(mac) : null);
        if (maker is not null) parts.Add(maker);
        if (d.Mac is { } m) parts.Add(m.ToString());
        return parts.Count == 0 ? d.Ip.ToString() : $"{d.Ip} ({string.Join(", ", parts)})";
    }
}
