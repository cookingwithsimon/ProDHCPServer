using System.Net;
using System.Net.Sockets;

namespace ProDHCPServer.Core.Net;

/// <summary>Small IPv4 helpers that work on host-order uint values.</summary>
public static class IPv4
{
    public static readonly IPAddress Zero = IPAddress.Any;
    public static readonly IPAddress Broadcast = IPAddress.Broadcast;

    public static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"'{address}' is not an IPv4 address.", nameof(address));
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public static IPAddress Parse(string text)
    {
        if (!TryParse(text, out var ip))
            throw new FormatException($"'{text}' is not a valid IPv4 address.");
        return ip;
    }

    public static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.Any;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!byte.TryParse(parts[i], out bytes[i])) return false;
        }
        address = new IPAddress(bytes);
        return true;
    }

    public static uint PrefixToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        return prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
    }

    public static IPAddress PrefixToMaskAddress(int prefixLength) => FromUInt32(PrefixToMask(prefixLength));

    /// <summary>Returns the prefix length for a contiguous mask, or -1 if the mask is not contiguous.</summary>
    public static int MaskToPrefix(IPAddress mask)
    {
        uint m = ToUInt32(mask);
        int prefix = System.Numerics.BitOperations.PopCount(m);
        return PrefixToMask(prefix) == m ? prefix : -1;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        uint mask = PrefixToMask(prefixLength);
        return (ToUInt32(a) & mask) == (ToUInt32(b) & mask);
    }

    public static IPAddress NetworkAddress(IPAddress ip, int prefixLength) =>
        FromUInt32(ToUInt32(ip) & PrefixToMask(prefixLength));

    public static IPAddress BroadcastAddress(IPAddress ip, int prefixLength) =>
        FromUInt32(ToUInt32(ip) | ~PrefixToMask(prefixLength));

    public static bool IsZero(IPAddress ip) => ToUInt32(ip) == 0;

    /// <summary>169.254.0.0/16, the address Windows and most devices pick when DHCP fails.</summary>
    public static bool IsApipa(IPAddress ip) => (ToUInt32(ip) & 0xFFFF0000) == 0xA9FE0000;

    public static bool IsLoopback(IPAddress ip) => (ToUInt32(ip) & 0xFF000000) == 0x7F000000;

    /// <summary>True when the two subnets share any addresses.</summary>
    public static bool SubnetsOverlap(IPAddress a, int prefixA, IPAddress b, int prefixB)
    {
        int shorter = Math.Min(prefixA, prefixB);
        return SameSubnet(a, b, shorter);
    }

    public static string ToCidr(IPAddress ip, int prefixLength) => $"{NetworkAddress(ip, prefixLength)}/{prefixLength}";
}
