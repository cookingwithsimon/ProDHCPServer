using System.Buffers.Binary;
using System.Net;
using System.Text;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Dhcp;

public sealed class DhcpParseException(string message) : Exception(message);

/// <summary>
/// A BOOTP/DHCP message (RFC 2131 section 2). Options keep their wire order; repeated
/// instances of the same option are concatenated on parse as RFC 3396 requires.
/// </summary>
public sealed class DhcpPacket
{
    public const int FixedHeaderLength = 236;
    public const int MinimumLength = FixedHeaderLength + 4; // header + magic cookie
    public const int BootpMinimumLength = 300;
    public const ushort BroadcastFlag = 0x8000;
    public static readonly byte[] MagicCookie = [99, 130, 83, 99];

    public BootOp Op { get; set; } = BootOp.Request;
    public byte HardwareType { get; set; } = 1; // Ethernet
    public byte HardwareLength { get; set; } = 6;
    public byte Hops { get; set; }
    public uint TransactionId { get; set; }
    public ushort Seconds { get; set; }
    public ushort Flags { get; set; }
    public IPAddress ClientIp { get; set; } = IPAddress.Any;      // ciaddr
    public IPAddress YourIp { get; set; } = IPAddress.Any;        // yiaddr
    public IPAddress NextServerIp { get; set; } = IPAddress.Any;  // siaddr
    public IPAddress RelayIp { get; set; } = IPAddress.Any;       // giaddr
    public byte[] ClientHardwareAddress { get; set; } = new byte[16];
    public string ServerName { get; set; } = "";
    public string BootFile { get; set; } = "";
    public List<DhcpOption> Options { get; } = [];

    /// <summary>False for plain BOOTP messages (no magic cookie or no message type option).</summary>
    public bool HasMagicCookie { get; set; } = true;

    public bool IsBroadcastFlagSet
    {
        get => (Flags & BroadcastFlag) != 0;
        set => Flags = value ? (ushort)(Flags | BroadcastFlag) : (ushort)(Flags & ~BroadcastFlag);
    }

    public MacAddress ClientMac => MacAddress.FromBytes(ClientHardwareAddress.AsSpan(0, 6));

    public DhcpMessageType? MessageType
    {
        get
        {
            var o = GetOption(DhcpOptionCode.MessageType);
            return o is { Data.Length: >= 1 } ? (DhcpMessageType)o.Data[0] : null;
        }
    }

    public IPAddress? RequestedIp => GetOption(DhcpOptionCode.RequestedIp)?.AsAddress();
    public IPAddress? ServerIdentifier => GetOption(DhcpOptionCode.ServerIdentifier)?.AsAddress();
    public string? HostName => GetOption(DhcpOptionCode.HostName)?.AsString();
    public string? VendorClass => GetOption(DhcpOptionCode.VendorClassId)?.AsString();
    public byte[]? ClientId => GetOption(DhcpOptionCode.ClientId)?.Data;
    public byte[] ParameterRequestList => GetOption(DhcpOptionCode.ParameterRequestList)?.Data ?? [];

    public ushort? MaxMessageSize
    {
        get
        {
            var o = GetOption(DhcpOptionCode.MaxMessageSize);
            return o is { Data.Length: 2 } ? BinaryPrimitives.ReadUInt16BigEndian(o.Data) : null;
        }
    }

    public DhcpOption? GetOption(byte code) => Options.FirstOrDefault(o => o.Code == code);

    public void SetOption(DhcpOption option)
    {
        int i = Options.FindIndex(o => o.Code == option.Code);
        if (i >= 0) Options[i] = option; else Options.Add(option);
    }

    public void RemoveOption(byte code) => Options.RemoveAll(o => o.Code == code);

    public static DhcpPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FixedHeaderLength)
            throw new DhcpParseException($"Packet is {data.Length} bytes; a DHCP/BOOTP message needs at least {FixedHeaderLength}.");

        var op = data[0];
        if (op is not (1 or 2)) throw new DhcpParseException($"Unknown BOOTP op code {op}.");

        var p = new DhcpPacket
        {
            Op = (BootOp)op,
            HardwareType = data[1],
            HardwareLength = data[2],
            Hops = data[3],
            TransactionId = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            Seconds = BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            ClientIp = new IPAddress(data.Slice(12, 4)),
            YourIp = new IPAddress(data.Slice(16, 4)),
            NextServerIp = new IPAddress(data.Slice(20, 4)),
            RelayIp = new IPAddress(data.Slice(24, 4)),
            ClientHardwareAddress = data.Slice(28, 16).ToArray(),
        };
        var sname = data.Slice(44, 64);
        var file = data.Slice(108, 128);

        if (data.Length < MinimumLength || !data.Slice(FixedHeaderLength, 4).SequenceEqual(MagicCookie))
        {
            p.HasMagicCookie = false;
            p.ServerName = ReadCString(sname);
            p.BootFile = ReadCString(file);
            return p;
        }

        var raw = new List<(byte Code, byte[] Data)>();
        ReadOptions(data[MinimumLength..], raw, "options");

        // Option 52: options continue in the file and/or sname fields (RFC 2132 9.3).
        var overload = raw.FirstOrDefault(o => o.Code == DhcpOptionCode.OptionOverload);
        int overloadValue = overload.Data is { Length: 1 } ? overload.Data[0] : 0;
        if ((overloadValue & 1) != 0) ReadOptions(file, raw, "file field"); else p.BootFile = ReadCString(file);
        if ((overloadValue & 2) != 0) ReadOptions(sname, raw, "sname field"); else p.ServerName = ReadCString(sname);

        // RFC 3396: concatenate repeated options, keeping the position of the first instance.
        foreach (var group in raw.Where(o => o.Code != DhcpOptionCode.OptionOverload).GroupBy(o => o.Code))
            p.Options.Add(new DhcpOption(group.Key, group.SelectMany(g => g.Data).ToArray()));

        return p;
    }

    private static void ReadOptions(ReadOnlySpan<byte> span, List<(byte, byte[])> into, string where)
    {
        int i = 0;
        while (i < span.Length)
        {
            byte code = span[i++];
            if (code == DhcpOptionCode.Pad) continue;
            if (code == DhcpOptionCode.End) return;
            if (i >= span.Length) throw new DhcpParseException($"Option {code} in the {where} is missing its length byte.");
            int len = span[i++];
            if (i + len > span.Length)
                throw new DhcpParseException($"Option {code} in the {where} says it is {len} bytes but only {span.Length - i} remain.");
            into.Add((code, span.Slice(i, len).ToArray()));
            i += len;
        }
        // A missing End option is tolerated: plenty of embedded stacks omit it.
    }

    private static string ReadCString(ReadOnlySpan<byte> span)
    {
        int end = span.IndexOf((byte)0);
        if (end < 0) end = span.Length;
        return Encoding.ASCII.GetString(span[..end]);
    }

    public byte[] Serialize(int minimumLength = BootpMinimumLength)
    {
        var buffer = new List<byte>(576);
        Span<byte> header = stackalloc byte[FixedHeaderLength];
        header.Clear();
        header[0] = (byte)Op;
        header[1] = HardwareType;
        header[2] = HardwareLength;
        header[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], Seconds);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], Flags);
        ClientIp.TryWriteBytes(header.Slice(12, 4), out _);
        YourIp.TryWriteBytes(header.Slice(16, 4), out _);
        NextServerIp.TryWriteBytes(header.Slice(20, 4), out _);
        RelayIp.TryWriteBytes(header.Slice(24, 4), out _);
        ClientHardwareAddress.AsSpan(0, Math.Min(16, ClientHardwareAddress.Length)).CopyTo(header.Slice(28, 16));
        WriteCString(header.Slice(44, 64), ServerName);
        WriteCString(header.Slice(108, 128), BootFile);
        buffer.AddRange(header.ToArray());

        if (HasMagicCookie)
        {
            buffer.AddRange(MagicCookie);
            foreach (var option in Options)
            {
                // Options longer than 255 bytes are split across several instances (RFC 3396).
                int offset = 0;
                do
                {
                    int chunk = Math.Min(255, option.Data.Length - offset);
                    buffer.Add(option.Code);
                    buffer.Add((byte)chunk);
                    buffer.AddRange(option.Data.AsSpan(offset, chunk).ToArray());
                    offset += chunk;
                } while (offset < option.Data.Length);
            }
            buffer.Add(DhcpOptionCode.End);
        }

        while (buffer.Count < minimumLength) buffer.Add(0);
        return buffer.ToArray();
    }

    private static void WriteCString(Span<byte> target, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length - 1)).CopyTo(target);
    }

    /// <summary>One-line description for logs, e.g. "DISCOVER from 00:11:22:33:44:55 xid 0x1234ABCD".</summary>
    public string Summary()
    {
        var type = MessageType?.ToString().ToUpperInvariant() ?? (Op == BootOp.Request ? "BOOTREQUEST" : "BOOTREPLY");
        var sb = new StringBuilder($"{type} {(Op == BootOp.Request ? "from" : "to")} {ClientMac} xid 0x{TransactionId:X8}");
        if (!IPv4.IsZero(ClientIp)) sb.Append($" ciaddr {ClientIp}");
        if (!IPv4.IsZero(YourIp)) sb.Append($" yiaddr {YourIp}");
        if (RequestedIp is { } r) sb.Append($" requested {r}");
        if (ServerIdentifier is { } s) sb.Append($" server {s}");
        if (IsBroadcastFlagSet) sb.Append(" [broadcast flag]");
        return sb.ToString();
    }

    /// <summary>Multi-line dump of every field and option, for the expandable technical detail.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary());
        sb.AppendLine($"  op={Op} htype={HardwareType} hlen={HardwareLength} hops={Hops} secs={Seconds} flags=0x{Flags:X4}");
        sb.AppendLine($"  ciaddr={ClientIp} yiaddr={YourIp} siaddr={NextServerIp} giaddr={RelayIp}");
        if (!string.IsNullOrEmpty(ServerName)) sb.AppendLine($"  sname={ServerName}");
        if (!string.IsNullOrEmpty(BootFile)) sb.AppendLine($"  file={BootFile}");
        foreach (var o in Options)
            sb.AppendLine($"  [{o.Code}] {DhcpOptionCode.Describe(o.Code)}: {o.Display()}");
        return sb.ToString();
    }
}
