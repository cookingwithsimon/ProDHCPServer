using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Discovery;

/// <summary>A query sent to find one kind of device.</summary>
public sealed record DiscoveryQuery(string Name, IPAddress Destination, int Port, byte[] Payload, int SourcePort = 0);

/// <summary>
/// Manufacturers' and industry discovery protocols. Each address, port and packet layout is from the
/// specification or maker's documentation (see docs/vendor-discovery.md); where a maker publishes the
/// address but not the packet layout, only the device's presence is recorded. Every query sent is
/// read-only: it asks devices to identify themselves and changes nothing.
/// </summary>
public static partial class VendorProtocols
{
    /// <summary>PJLink Class 2 search (JBMIA PJLink spec, 3.2): projectors and displays.</summary>
    public const int PjLinkPort = 4352;
    /// <summary>Art-Net (Artistic Licence spec): lighting nodes and LED processors.</summary>
    public const int ArtNetPort = 6454;
    /// <summary>SAP (RFC 2974): AES67 stream announcements, from Dante, Ravenna, Q-SYS and others.</summary>
    public const int SapPort = 9875;
    /// <summary>AMX Device Discovery beacons (AMX AV/IT Administrators Guide): projectors and displays that want AMX controllers to find them.</summary>
    public const int AmxBeaconPort = 9131;
    /// <summary>AMX NetLinx controllers broadcast from and to this port every five seconds (same guide).</summary>
    public const int AmxNetLinxPort = 1319;
    /// <summary>Q-SYS Discovery Protocol (QSC help: "Multicast UDP 224.0.23.175 on Ports 2467-2470").</summary>
    public const int QdpPort = 2467;
    /// <summary>Shure's service discovery (SLP on 239.255.254.253:8427, Shure "Device IP Ports and Protocols").</summary>
    public const int ShureSlpPort = 8427;

    private static readonly byte[] PjLinkSearch = "%2SRCH\r"u8.ToArray();

    // "Art-Net\0", OpPoll 0x2000 (low byte first), protocol version 14, flags 0 (no unsolicited replies, no diagnostics), priority 0.
    private static readonly byte[] ArtPoll = [0x41, 0x72, 0x74, 0x2D, 0x4E, 0x65, 0x74, 0x00, 0x00, 0x20, 0x00, 0x0E, 0x00, 0x00];

    /// <summary>UDP ports to listen on for announcements and replies.</summary>
    public static IReadOnlyList<int> ListenPorts { get; } = [PjLinkPort, ArtNetPort, SapPort, AmxBeaconPort, AmxNetLinxPort, QdpPort, ShureSlpPort];

    /// <summary>Short names for the log.</summary>
    public static IReadOnlyList<string> Names { get; } = ["PJLink", "Art-Net", "AES67 (SAP)", "AMX beacons", "Q-SYS", "Shure"];

    /// <summary>Multicast groups to join for a listen port, on the selected adapter.</summary>
    public static IReadOnlyList<IPAddress> Groups(int port) => port switch
    {
        SapPort => [IPAddress.Parse("239.255.255.255")],
        AmxBeaconPort => [IPAddress.Parse("239.255.250.250")],
        AmxNetLinxPort => [IPAddress.Parse("239.255.250.251")],
        QdpPort => [IPAddress.Parse("224.0.23.175")],
        ShureSlpPort => [IPAddress.Parse("239.255.254.253")],
        _ => [],
    };

    /// <summary>
    /// The queries for one round: PJLink search and Art-Net ArtPoll to each of the adapter's subnet broadcasts.
    /// PJLink also goes to 255.255.255.255 to reach projectors on other subnets; Art-Net must not (its spec forbids it).
    /// </summary>
    public static IReadOnlyList<DiscoveryQuery> Queries(IEnumerable<AddressAssignment> local)
    {
        var list = new List<DiscoveryQuery>();
        foreach (var a in local.Where(a => a.PrefixLength is > 0 and < 31))
        {
            var broadcast = IPv4.BroadcastAddress(a.Address, a.PrefixLength);
            list.Add(new("PJLink", broadcast, PjLinkPort, PjLinkSearch, PjLinkPort));
            list.Add(new("Art-Net", broadcast, ArtNetPort, ArtPoll, ArtNetPort));
        }
        list.Add(new("PJLink", IPAddress.Broadcast, PjLinkPort, PjLinkSearch, PjLinkPort));
        return list;
    }

    public static Sighting? TryParse(DateTimeOffset at, IPAddress source, int sourcePort, int destinationPort, ReadOnlySpan<byte> payload)
    {
        bool Either(int port) => sourcePort == port || destinationPort == port;
        if (Either(PjLinkPort)) return PjLink(at, source, payload);
        if (Either(ArtNetPort)) return ArtNet(at, source, payload);
        if (destinationPort == SapPort) return Sap(at, source, payload);
        if (destinationPort == AmxBeaconPort) return AmxBeacon(at, source, payload);
        if (sourcePort == AmxNetLinxPort && destinationPort == AmxNetLinxPort)
            return new Sighting(at, DiscoverySource.Vendor, source) { Label = "AMX NetLinx", Manufacturer = "AMX", Detail = "AMX NetLinx controller announcement (UDP 1319)" };
        if (destinationPort is >= QdpPort and <= QdpPort + 3) return Qdp(at, source, payload);
        if (destinationPort == ShureSlpPort) return ShureSlp(at, source, payload);
        return null;
    }

    private static Sighting? PjLink(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> payload)
    {
        var text = Encoding.ASCII.GetString(payload).TrimEnd('\r', '\n', '\0');
        if (!text.StartsWith("%2ACKN=", StringComparison.OrdinalIgnoreCase)) return null; // another controller's search
        MacAddress.TryParse(text[7..].Trim(), out var mac);
        return new Sighting(at, DiscoverySource.Vendor, source)
        {
            Label = "PJLink",
            Mac = mac == MacAddress.Empty ? null : mac,
            Detail = "Answered the PJLink Class 2 search (a projector or display)",
        };
    }

    private static Sighting? ArtNet(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> p)
    {
        // ArtPollReply: OpCode 0x2100, at least 207 bytes.
        if (p.Length < 207 || !p[..8].SequenceEqual("Art-Net\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(p[8..]) != 0x2100) return null;
        var shortName = CString(p.Slice(26, 18));
        var longName = CString(p.Slice(44, 64));
        var mac = MacAddress.FromBytes(p.Slice(201, 6));
        return new Sighting(at, DiscoverySource.Vendor, source)
        {
            Label = "Art-Net",
            Name = longName ?? shortName,
            Mac = mac == MacAddress.Empty ? null : mac,
            Detail = $"Art-Net node \"{shortName}\", firmware {p[16]}.{p[17]}, ESTA code {BinaryPrimitives.ReadUInt16LittleEndian(p[24..]):X4}",
        };
    }

    private static Sighting? Sap(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> p)
    {
        if (p.Length < 8) return null;
        int flags = p[0];
        if (flags >> 5 != 1 || (flags & 0x10) != 0 || (flags & 0x04) != 0 || (flags & 0x02) != 0) return null; // not v1, IPv6, deletion or encrypted
        int offset = 4 + 4 + p[1] * 4;
        if (offset >= p.Length) return null;
        byte[] body = p[offset..].ToArray();
        if ((flags & 0x01) != 0)
        {
            try
            {
                using var z = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                body = ms.ToArray();
            }
            catch (InvalidDataException) { return null; }
        }
        var text = Encoding.UTF8.GetString(body);
        if (!text.StartsWith("v=", StringComparison.Ordinal))
        {
            int nul = text.IndexOf('\0');
            if (nul < 0) return null;
            text = text[(nul + 1)..];
        }
        string? Line(string key) => text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?[key.Length..].Trim();
        var stream = Line("s=");
        // o=<username> <sess-id> <sess-version> IN IP4 <unicast address>: the device that sends the stream.
        var origin = Line("o=")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sender = origin is { Length: 6 } && origin[4] == "IP4" && IPv4.TryParse(origin[5], out var o) ? o : new IPAddress(p.Slice(4, 4));
        if (!DiscoveryTable.IsDeviceAddress(sender)) sender = source;
        return new Sighting(at, DiscoverySource.Vendor, sender)
        {
            Label = "AES67",
            Detail = $"Announces an AES67 stream{(stream is { Length: > 0 } ? $" \"{stream}\"" : "")} (SAP)",
        };
    }

    [GeneratedRegex(@"<-?([A-Za-z][A-Za-z-]*)=([^>]*)>")]
    private static partial Regex AmxField();

    private static Sighting AmxBeacon(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> p)
    {
        // AMX documents the field names (UUID = MAC, SDKClass, Make, Model, Revision, Config-URL) but not the framing,
        // so any <Key=Value> pair is accepted.
        var text = Encoding.ASCII.GetString(p.Length > 1024 ? p[..1024] : p);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AmxField().Matches(text))
        {
            var key = m.Groups[1].Value;
            if (key.StartsWith("Device-", StringComparison.OrdinalIgnoreCase)) key = key[7..];
            fields.TryAdd(key, m.Groups[2].Value.Trim());
        }
        string? F(string k) => fields.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        MacAddress? mac = MacAddress.TryParse(F("UUID"), out var parsed) && parsed != MacAddress.Empty ? parsed : null;
        Uri? web = Uri.TryCreate(F("Config-URL"), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            && IPAddress.TryParse(u.Host, out var host) && host.Equals(source) ? u : null;
        var cls = F("SDKClass");
        return new Sighting(at, DiscoverySource.Vendor, source)
        {
            Label = "AMX beacon",
            Mac = mac,
            Manufacturer = F("Make"),
            Model = F("Model"),
            WebPage = web,
            Detail = fields.Count == 0
                ? $"AMX Device Discovery beacon: {Printable(text)}"
                : $"AMX Device Discovery beacon{(cls is null ? "" : $" ({cls})")}{(F("Revision") is { } rev ? $", firmware {rev}" : "")}",
        };
    }

    [GeneratedRegex(@"<(name|type|part_number)>([^<]{1,80})</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex QdpField();

    private static Sighting Qdp(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> p)
    {
        // QSC doesn't publish the packet layout; names are read best-effort when the payload looks like XML.
        string? name = null, type = null, part = null;
        if (p.Length > 0 && p[0] == (byte)'<')
        {
            foreach (Match m in QdpField().Matches(Encoding.UTF8.GetString(p.Length > 4096 ? p[..4096] : p)))
            {
                var v = m.Groups[2].Value.Trim();
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "name": name ??= v; break;
                    case "type": type ??= v; break;
                    case "part_number": part ??= v; break;
                }
            }
        }
        return new Sighting(at, DiscoverySource.Vendor, source)
        {
            Label = "Q-SYS",
            Name = name,
            Model = part ?? type,
            Manufacturer = "QSC",
            Detail = name is null ? "Q-SYS discovery announcement (QDP)" : $"Q-SYS discovery announcement (QDP), as reported by the device: {name}{(type is null ? "" : $", {type}")}",
        };
    }

    [GeneratedRegex(@"service:[A-Za-z0-9.+\-:/_@]{1,120}")]
    private static partial Regex SlpUrl();

    private static Sighting? ShureSlp(DateTimeOffset at, IPAddress source, ReadOnlySpan<byte> p)
    {
        // SLPv2 header (RFC 2608 section 8): version 2, then the function ID.
        if (p.Length < 14 || p[0] != 2) return null;
        var urls = SlpUrl().Matches(Encoding.ASCII.GetString(p)).Select(m => m.Value).Distinct().Take(3).ToList();
        return new Sighting(at, DiscoverySource.Vendor, source)
        {
            Label = "Shure",
            Detail = urls.Count > 0 ? $"Shure discovery (SLP): {string.Join(", ", urls)}" : "Shure discovery (SLP on 239.255.254.253:8427)",
        };
    }

    private static string? CString(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        var s = Encoding.ASCII.GetString(nul >= 0 ? field[..nul] : field).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string Printable(string text)
    {
        var s = new string(text.Select(c => c is >= ' ' and <= '~' ? c : '.').ToArray());
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
