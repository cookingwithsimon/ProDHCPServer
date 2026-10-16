using System.Buffers.Binary;
using System.Net;
using System.Text;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Discovery;

/// <summary>What a managed switch says about the port the laptop is plugged into (from LLDP or CDP).</summary>
public sealed record SwitchPortInfo
{
    /// <summary>"LLDP" or "CDP".</summary>
    public required string Protocol { get; init; }
    public MacAddress SenderMac { get; init; }
    public string? SwitchName { get; init; }
    public string? Port { get; init; }
    public string? PortDescription { get; init; }
    /// <summary>System description (LLDP) or platform (CDP), often the model and firmware.</summary>
    public string? Model { get; init; }
    public IPAddress? ManagementIp { get; init; }
    /// <summary>The untagged (native/port) VLAN.</summary>
    public int? Vlan { get; init; }
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>One line, like "Plugged into core-sw1 port Gi1/0/5 (Rack 3), VLAN 20".</summary>
    public string Describe()
    {
        var sb = new StringBuilder("Plugged into ");
        sb.Append(SwitchName is { Length: > 0 } n ? n : ManagementIp?.ToString() ?? $"a switch ({SenderMac})");
        if (Port is { Length: > 0 } p) sb.Append(" port ").Append(p);
        if (PortDescription is { Length: > 0 } d && d != Port) sb.Append(" (").Append(d).Append(')');
        if (Vlan is { } v) sb.Append(", VLAN ").Append(v);
        return sb.ToString();
    }

    public string DetailText()
    {
        var lines = new List<string> { $"Heard via {Protocol} from {SenderMac}" };
        if (SwitchName is not null) lines.Add($"Switch name: {SwitchName}");
        if (Port is not null) lines.Add($"Port: {Port}");
        if (PortDescription is not null) lines.Add($"Port description: {PortDescription}");
        if (Vlan is not null) lines.Add($"VLAN: {Vlan}");
        if (ManagementIp is not null) lines.Add($"Management address: {ManagementIp}");
        if (Model is not null) lines.Add($"Model: {Model}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>IEEE 802.1AB Link Layer Discovery Protocol.</summary>
public static class Lldp
{
    /// <summary>The nearest-bridge multicast address LLDP is sent to.</summary>
    public static readonly MacAddress Multicast = MacAddress.Parse("01:80:C2:00:00:0E");

    public static SwitchPortInfo? TryParse(ReadOnlySpan<byte> data, MacAddress sender)
    {
        var info = new SwitchPortInfo { Protocol = "LLDP", SenderMac = sender };
        string? chassis = null;
        bool sawChassis = false;
        int offset = 0;
        while (offset + 2 <= data.Length)
        {
            ushort header = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int type = header >> 9, length = header & 0x1FF;
            offset += 2;
            if (type == 0) break;
            if (offset + length > data.Length) return null;
            var value = data.Slice(offset, length);
            offset += length;
            switch (type)
            {
                case 1 when value.Length >= 2:
                    sawChassis = true;
                    chassis = value[0] == 4 && value.Length >= 7 ? MacAddress.FromBytes(value[1..]).ToString() : Text(value[1..]);
                    break;
                case 2 when value.Length >= 2:
                    info = info with { Port = value[0] == 3 && value.Length >= 7 ? MacAddress.FromBytes(value[1..]).ToString() : Text(value[1..]) };
                    break;
                case 3 when value.Length >= 2:
                    info = info with { TimeToLive = TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt16BigEndian(value)) };
                    break;
                case 4: info = info with { PortDescription = Text(value) }; break;
                case 5: info = info with { SwitchName = Text(value) }; break;
                case 6: info = info with { Model = Text(value) }; break;
                case 8 when value.Length >= 6 && value[1] == 1 && value[0] >= 5:
                    info = info with { ManagementIp = new IPAddress(value.Slice(2, 4)) };
                    break;
                case 127 when value.Length >= 6 && value[0] == 0x00 && value[1] == 0x80 && value[2] == 0xC2 && value[3] == 1:
                    int pvid = BinaryPrimitives.ReadUInt16BigEndian(value[4..]);
                    if (pvid != 0) info = info with { Vlan = pvid };
                    break;
            }
        }
        if (!sawChassis) return null;
        return info.SwitchName is null && chassis is not null ? info with { SwitchName = chassis } : info;
    }

    internal static string? Text(ReadOnlySpan<byte> value)
    {
        var s = Encoding.UTF8.GetString(value).Trim('\0', ' ', '\r', '\n');
        s = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? null : s.Length > 200 ? s[..200] + "…" : s;
    }
}

/// <summary>Cisco Discovery Protocol (version 1 and 2), sent by Cisco and some other switches.</summary>
public static class Cdp
{
    public static readonly MacAddress Multicast = MacAddress.Parse("01:00:0C:CC:CC:CC");

    public static SwitchPortInfo? TryParse(ReadOnlySpan<byte> data, MacAddress sender)
    {
        if (data.Length < 4 || data[0] is not (1 or 2)) return null;
        var info = new SwitchPortInfo { Protocol = "CDP", SenderMac = sender, TimeToLive = TimeSpan.FromSeconds(data[1]) };
        int offset = 4;
        while (offset + 4 <= data.Length)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (length < 4 || offset + length > data.Length) break;
            var value = data.Slice(offset + 4, length - 4);
            offset += length;
            switch (type)
            {
                case 0x0001: info = info with { SwitchName = Lldp.Text(value) }; break;
                case 0x0002 or 0x0016 when info.ManagementIp is null || type == 0x0016:
                    if (FirstIPv4(value) is { } ip) info = info with { ManagementIp = ip };
                    break;
                case 0x0003: info = info with { Port = Lldp.Text(value) }; break;
                case 0x0006: info = info with { Model = Lldp.Text(value) }; break;
                case 0x000A when value.Length >= 2: info = info with { Vlan = BinaryPrimitives.ReadUInt16BigEndian(value) }; break;
            }
        }
        return info.SwitchName is null && info.Port is null ? null : info;
    }

    private static IPAddress? FirstIPv4(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4) return null;
        int count = BinaryPrimitives.ReadInt32BigEndian(value);
        int offset = 4;
        for (int i = 0; i < count && offset + 2 <= value.Length; i++)
        {
            int protoLength = value[offset + 1];
            int addrLenAt = offset + 2 + protoLength;
            if (addrLenAt + 2 > value.Length) return null;
            int addrLength = BinaryPrimitives.ReadUInt16BigEndian(value[addrLenAt..]);
            int addrAt = addrLenAt + 2;
            if (addrAt + addrLength > value.Length) return null;
            // Protocol type 1 (NLPID) with protocol 0xCC is IPv4.
            if (value[offset] == 1 && protoLength == 1 && value[offset + 2] == 0xCC && addrLength == 4)
                return new IPAddress(value.Slice(addrAt, 4));
            offset = addrAt + addrLength;
        }
        return null;
    }
}
