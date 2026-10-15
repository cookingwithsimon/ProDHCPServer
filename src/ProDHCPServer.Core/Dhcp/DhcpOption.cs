using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ProDHCPServer.Core.Dhcp;

public sealed record DhcpOption(byte Code, byte[] Data)
{
    public static DhcpOption FromByte(byte code, byte value) => new(code, [value]);

    public static DhcpOption FromUInt32(byte code, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return new(code, b);
    }

    public static DhcpOption FromUInt16(byte code, ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return new(code, b);
    }

    public static DhcpOption FromAddress(byte code, IPAddress address) => new(code, address.GetAddressBytes());

    public static DhcpOption FromAddresses(byte code, IEnumerable<IPAddress> addresses) =>
        new(code, addresses.SelectMany(a => a.GetAddressBytes()).ToArray());

    public static DhcpOption FromString(byte code, string value) => new(code, Encoding.ASCII.GetBytes(value));

    public IPAddress? AsAddress() => Data.Length >= 4 ? new IPAddress(Data.AsSpan(0, 4)) : null;

    public IReadOnlyList<IPAddress> AsAddresses() =>
        Enumerable.Range(0, Data.Length / 4).Select(i => new IPAddress(Data.AsSpan(i * 4, 4))).ToList();

    public uint? AsUInt32() => Data.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(Data) : null;

    /// <summary>Text options (host name, vendor class) are often NUL-padded by embedded devices.</summary>
    public string AsString() => Encoding.ASCII.GetString(Data).TrimEnd('\0').Trim();

    public string Display() => Code switch
    {
        DhcpOptionCode.MessageType when Data.Length == 1 => ((DhcpMessageType)Data[0]).ToString(),
        DhcpOptionCode.SubnetMask or DhcpOptionCode.RequestedIp or DhcpOptionCode.ServerIdentifier
            or DhcpOptionCode.BroadcastAddress when Data.Length == 4 => AsAddress()!.ToString(),
        DhcpOptionCode.Router or DhcpOptionCode.DnsServers or DhcpOptionCode.NtpServers
            or DhcpOptionCode.TftpServerAddress when Data.Length % 4 == 0 => string.Join(", ", AsAddresses()),
        DhcpOptionCode.LeaseTime or DhcpOptionCode.RenewalTime or DhcpOptionCode.RebindingTime
            when Data.Length == 4 => $"{AsUInt32()} s",
        DhcpOptionCode.HostName or DhcpOptionCode.DomainName or DhcpOptionCode.VendorClassId
            or DhcpOptionCode.TftpServerName or DhcpOptionCode.BootFileName or DhcpOptionCode.Message => $"\"{AsString()}\"",
        DhcpOptionCode.ParameterRequestList => string.Join(", ", Data),
        _ => Convert.ToHexString(Data),
    };
}
