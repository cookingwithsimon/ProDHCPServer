using System.Globalization;

namespace ProDHCPServer.Core.Net;

/// <summary>A 48-bit Ethernet MAC address.</summary>
public readonly record struct MacAddress(ulong Value) : IComparable<MacAddress>
{
    public static readonly MacAddress Empty = new(0);

    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6) throw new ArgumentException("A MAC address needs 6 bytes.", nameof(bytes));
        ulong v = 0;
        for (int i = 0; i < 6; i++) v = (v << 8) | bytes[i];
        return new MacAddress(v);
    }

    public byte[] ToBytes()
    {
        var b = new byte[6];
        for (int i = 5; i >= 0; i--) b[5 - i] = (byte)(Value >> (i * 8));
        return b;
    }

    /// <summary>The first three bytes, as used for manufacturer lookup.</summary>
    public uint Oui => (uint)(Value >> 24);

    public bool IsBroadcast => Value == 0xFFFF_FFFF_FFFF;

    /// <summary>Locally administered addresses (randomised/virtual) never match a manufacturer.</summary>
    public bool IsLocallyAdministered => ((Value >> 40) & 0x02) != 0;

    public static bool TryParse(string? text, out MacAddress mac)
    {
        mac = Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        var separators = text.Count(c => !Uri.IsHexDigit(c));
        if (hex.Length != 12 || separators is not (0 or 2 or 5)) return false;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        mac = new MacAddress(v);
        return true;
    }

    public static MacAddress Parse(string text) =>
        TryParse(text, out var mac) ? mac : throw new FormatException($"'{text}' is not a valid MAC address.");

    public override string ToString() => ToString(':');

    public string ToString(char separator)
    {
        var b = ToBytes();
        return string.Join(separator, b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public int CompareTo(MacAddress other) => Value.CompareTo(other.Value);
}
