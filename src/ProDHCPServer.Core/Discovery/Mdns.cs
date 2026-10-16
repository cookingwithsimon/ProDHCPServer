using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ProDHCPServer.Core.Discovery;

public enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

/// <summary>One resource record from a DNS message, with the common types decoded.</summary>
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl)
{
    public IPAddress? Address { get; init; }
    /// <summary>PTR target, or SRV target host.</summary>
    public string? Target { get; init; }
    public int Port { get; init; }
    public IReadOnlyList<string> Text { get; init; } = [];
}

/// <summary>A minimal DNS message codec for multicast DNS (RFC 6762) and DNS-SD (RFC 6763).</summary>
public static class DnsMessage
{
    public const int MdnsPort = 5353;
    public static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    /// <summary>A standard query for PTR records of each name (for example "_http._tcp.local").</summary>
    public static byte[] BuildQuery(IEnumerable<string> names, DnsType type = DnsType.Ptr)
    {
        var list = names.ToList();
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)list.Count);
        ms.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var name in list)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length is 0 or > 63) throw new ArgumentException($"Bad DNS label in \"{name}\".", nameof(names));
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes);
            }
            ms.WriteByte(0);
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // class IN, multicast response wanted
            ms.Write(tail);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// The answer, authority and additional records of a response. Returns null for queries and
    /// for anything that isn't a well-formed DNS message.
    /// </summary>
    public static IReadOnlyList<DnsRecord>? TryParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if ((flags & 0x8000) == 0) return null; // a query, not a response
        int questions = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) + BinaryPrimitives.ReadUInt16BigEndian(data[8..]) + BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        int offset = 12;
        var result = new List<DnsRecord>();
        try
        {
            for (int i = 0; i < questions; i++)
            {
                ReadName(data, ref offset);
                offset += 4;
            }
            for (int i = 0; i < records && i < 200; i++)
            {
                var name = ReadName(data, ref offset);
                if (offset + 10 > data.Length) return null;
                var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
                offset += 10;
                if (offset + length > data.Length) return null;
                var rdata = data.Slice(offset, length);
                var record = new DnsRecord(name, type, ttl);
                switch (type)
                {
                    case DnsType.A when length == 4:
                        record = record with { Address = new IPAddress(rdata) };
                        break;
                    case DnsType.Ptr:
                        int at = offset;
                        record = record with { Target = ReadName(data, ref at) };
                        break;
                    case DnsType.Srv when length >= 7:
                        int target = offset + 6;
                        record = record with { Port = BinaryPrimitives.ReadUInt16BigEndian(rdata[4..]), Target = ReadName(data, ref target) };
                        break;
                    case DnsType.Txt:
                        record = record with { Text = ReadText(rdata) };
                        break;
                }
                result.Add(record);
                offset += length;
            }
        }
        catch (FormatException)
        {
            return null;
        }
        return result;
    }

    private static IReadOnlyList<string> ReadText(ReadOnlySpan<byte> rdata)
    {
        var list = new List<string>();
        int i = 0;
        while (i < rdata.Length)
        {
            int len = rdata[i++];
            if (i + len > rdata.Length) break;
            if (len > 0) list.Add(Encoding.UTF8.GetString(rdata.Slice(i, len)));
            i += len;
        }
        return list;
    }

    /// <summary>Reads a possibly compressed name; leaves <paramref name="offset"/> just past it.</summary>
    internal static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        int pos = offset;
        bool jumped = false;
        int jumps = 0;
        while (true)
        {
            if (pos >= data.Length) throw new FormatException("Name runs past the end.");
            int len = data[pos];
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length || ++jumps > 20) throw new FormatException("Bad compression pointer.");
                if (!jumped) offset = pos + 2;
                jumped = true;
                pos = ((len & 0x3F) << 8) | data[pos + 1];
                continue;
            }
            if (len > 63) throw new FormatException("Bad label length.");
            pos++;
            if (len == 0) break;
            if (pos + len > data.Length) throw new FormatException("Label runs past the end.");
            labels.Add(Encoding.UTF8.GetString(data.Slice(pos, len)));
            pos += len;
        }
        if (!jumped) offset = pos;
        return string.Join('.', labels);
    }
}

/// <summary>What one mDNS response says about the device that sent it.</summary>
public sealed record MdnsAnnouncement(
    string? HostName,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<MdnsService> Services,
    IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>Service types the device lists in answer to "_services._dns-sd._udp.local", like "_http._tcp".</summary>
    public IReadOnlyList<string> AdvertisedTypes { get; init; } = [];
}

/// <summary>A DNS-SD service instance, like "Stage Box._netaudio-arc._udp.local" on port 4440.</summary>
public sealed record MdnsService(string Instance, string Type, int Port);

public static class MdnsInterpreter
{
    /// <summary>TXT keys that vendors use for the model or manufacturer.</summary>
    private static readonly string[] ModelKeys = ["model", "md", "mdl", "ty", "product", "modelname", "model_name", "am"];
    private static readonly string[] MakerKeys = ["manufacturer", "mf", "mfg", "vendor", "usb_mfg", "brand"];

    public static MdnsAnnouncement Interpret(IReadOnlyList<DnsRecord> records)
    {
        var addresses = records.Where(r => r.Type == DnsType.A && r.Address is not null && r.Ttl > 0).Select(r => r.Address!).Distinct().ToList();
        var srv = records.Where(r => r.Type == DnsType.Srv && r.Ttl > 0).ToList();
        string? host = srv.Select(s => s.Target).FirstOrDefault(t => !string.IsNullOrEmpty(t))
            ?? records.FirstOrDefault(r => r.Type == DnsType.A && r.Ttl > 0)?.Name;

        var services = new List<MdnsService>();
        foreach (var s in srv)
        {
            var (instance, type) = SplitInstance(s.Name);
            if (type is not null) services.Add(new MdnsService(instance, type, s.Port));
        }
        // PTR records name instances even when the SRV comes in a later packet.
        foreach (var ptr in records.Where(r => r.Type == DnsType.Ptr && r.Target is not null && r.Ttl > 0 && !r.Name.StartsWith("_services.", StringComparison.Ordinal)))
        {
            var (instance, type) = SplitInstance(ptr.Target!);
            if (type is not null && !services.Any(x => x.Type == type && x.Instance == instance)) services.Add(new MdnsService(instance, type, 0));
        }

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var txt in records.Where(r => r.Type == DnsType.Txt))
        {
            foreach (var entry in txt.Text)
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var key = entry[..eq].Trim();
                var value = entry[(eq + 1)..].Trim();
                if (value.Length > 0 && value.Length <= 200) props.TryAdd(key, value);
            }
        }
        var advertised = records
            .Where(r => r.Type == DnsType.Ptr && r.Target is not null && r.Name.StartsWith("_services._dns-sd._udp", StringComparison.OrdinalIgnoreCase))
            .Select(r => StripLocal(r.Target!)).Where(t => t.StartsWith('_') && (t.EndsWith("._tcp", StringComparison.Ordinal) || t.EndsWith("._udp", StringComparison.Ordinal)))
            .Distinct().ToList();
        return new MdnsAnnouncement(host is null ? null : StripLocal(host), addresses, services, props) { AdvertisedTypes = advertised };
    }

    public static string? Model(IReadOnlyDictionary<string, string> props) =>
        ModelKeys.Select(k => props.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v is not null);

    public static string? Manufacturer(IReadOnlyDictionary<string, string> props) =>
        MakerKeys.Select(k => props.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v is not null);

    /// <summary>"Stage Box._netaudio-arc._udp.local" → ("Stage Box", "_netaudio-arc._udp").</summary>
    public static (string Instance, string? Type) SplitInstance(string name)
    {
        var trimmed = StripLocal(name);
        var parts = trimmed.Split('.');
        // The service type is the last two labels: _service._tcp or _service._udp.
        if (parts.Length >= 3 && parts[^1] is "_tcp" or "_udp" && parts[^2].StartsWith('_'))
            return (string.Join('.', parts[..^2]), $"{parts[^2]}.{parts[^1]}");
        return (trimmed, null);
    }

    private static string StripLocal(string name) =>
        name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name.TrimEnd('.');
}

/// <summary>The mDNS service types asked about on every round, before any device has listed its own.</summary>
public static class MdnsCatalog
{
    /// <summary>The DNS-SD meta-query: every responder lists the service types it offers.</summary>
    public const string AllServices = "_services._dns-sd._udp.local";

    /// <summary>
    /// Only types that a standard or the maker documents. Anything else a device offers comes back from the
    /// <see cref="AllServices"/> query and is asked about on the next round.
    /// </summary>
    public static IReadOnlyList<string> ServiceTypes { get; } =
    [
        AllServices,
        "_http._tcp.local",
        "_https._tcp.local",
        // Sennheiser Sound Control Protocol (SSC spec 6.2): the instance name is the device name.
        "_ssc._udp.local",
        "_ssc._tcp.local",
        // Audinate Dante Domain Manager: devices.
        "_dante-ddm-d._udp.local",
    ];

    /// <summary>A label for well-documented service types, else null.</summary>
    public static string? Label(string type) => type switch
    {
        "_ssc._udp" or "_ssc._tcp" => "Sennheiser SSC",
        "_dante-ddm-d._udp" => "Dante",
        _ => null,
    };
}
