using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Profiles;

public enum CustomOptionFormat
{
    /// <summary>ASCII text, like a server name or URL.</summary>
    Text,
    /// <summary>One or more IPv4 addresses, separated by commas.</summary>
    Addresses,
    /// <summary>Raw bytes as hex, like "01 04 C0 A8 64 0A".</summary>
    Hex,
}

/// <summary>A DHCP option that has no field of its own in the profile form.</summary>
public sealed record CustomOption(byte Code, CustomOptionFormat Format, string Value);

/// <summary>
/// A saved set of server settings: subnet, pool, lease time and the options handed to devices.
/// Strings are kept as typed so a half-finished profile can still be saved and fixed later;
/// <see cref="ToConfig"/> checks them.
/// </summary>
public sealed record Profile
{
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public string ServerIp { get; init; } = "192.168.100.1";
    public int PrefixLength { get; init; } = 24;
    public string PoolStart { get; init; } = "192.168.100.100";
    public string PoolEnd { get; init; } = "192.168.100.199";
    public int LeaseMinutes { get; init; } = 60;
    /// <summary>Option 3.</summary>
    public string? Router { get; init; }
    /// <summary>Option 6, comma separated.</summary>
    public string? DnsServers { get; init; }
    /// <summary>Option 15.</summary>
    public string? DomainName { get; init; }
    /// <summary>Option 42, comma separated.</summary>
    public string? NtpServers { get; init; }
    /// <summary>Option 66: TFTP or provisioning server name, address or URL.</summary>
    public string? TftpServerName { get; init; }
    /// <summary>Option 150 (Cisco): TFTP server addresses, comma separated.</summary>
    public string? TftpServers { get; init; }
    /// <summary>Option 43: vendor-specific bytes as hex.</summary>
    public string? VendorSpecificHex { get; init; }
    public IReadOnlyList<CustomOption> CustomOptions { get; init; } = [];
    public bool AlwaysBroadcast { get; init; }

    /// <summary>Shipped with the app; read-only. Duplicate it to change it.</summary>
    [JsonIgnore] public bool IsPreset { get; init; }

    /// <summary>Codes that have their own field, or that the server sets itself, so can't be custom options.</summary>
    public static string? ReservedOptionReason(byte code) => code switch
    {
        0 or 255 => "is a padding/end marker, not a real option",
        DhcpOptionCode.SubnetMask => "is set from the subnet field",
        DhcpOptionCode.Router => "has its own field (Gateway)",
        DhcpOptionCode.DnsServers => "has its own field (DNS servers)",
        DhcpOptionCode.DomainName => "has its own field (Domain name)",
        DhcpOptionCode.BroadcastAddress => "is set from the subnet field",
        DhcpOptionCode.NtpServers => "has its own field (NTP servers)",
        DhcpOptionCode.VendorSpecific => "has its own field (Vendor-specific)",
        DhcpOptionCode.TftpServerName => "has its own field (TFTP or provisioning server)",
        DhcpOptionCode.TftpServerAddress => "has its own field (TFTP server addresses)",
        DhcpOptionCode.RequestedIp or DhcpOptionCode.LeaseTime or DhcpOptionCode.OptionOverload or DhcpOptionCode.MessageType
            or DhcpOptionCode.ServerIdentifier or DhcpOptionCode.ParameterRequestList or DhcpOptionCode.MaxMessageSize
            or DhcpOptionCode.RenewalTime or DhcpOptionCode.RebindingTime or DhcpOptionCode.ClientId => "is set by the server itself",
        _ => null,
    };

    /// <summary>Builds the server config, or returns plain-English problems.</summary>
    public (DhcpServerConfig? Config, IReadOnlyList<string> Errors) ToConfig()
    {
        var errors = new List<string>();
        var server = Address(ServerIp, "Server IP", errors);
        var start = Address(PoolStart, "Pool start", errors);
        var end = Address(PoolEnd, "Pool end", errors);
        var router = string.IsNullOrWhiteSpace(Router) ? null : Address(Router, "Gateway", errors);
        var dns = Addresses(DnsServers, "DNS server", errors);
        var ntp = Addresses(NtpServers, "NTP server", errors);
        var tftp = Addresses(TftpServers, "Option 150 address", errors);

        var extra = new Dictionary<byte, byte[]>();
        if (ntp.Count > 0) extra[DhcpOptionCode.NtpServers] = ToBytes(ntp);
        if (!string.IsNullOrWhiteSpace(TftpServerName)) extra[DhcpOptionCode.TftpServerName] = Text(TftpServerName, "Option 66", errors);
        if (tftp.Count > 0) extra[DhcpOptionCode.TftpServerAddress] = ToBytes(tftp);
        if (!string.IsNullOrWhiteSpace(VendorSpecificHex))
        {
            if (TryParseHex(VendorSpecificHex, out var v43)) extra[DhcpOptionCode.VendorSpecific] = Fit(v43, "Option 43", errors);
            else errors.Add($"Option 43 \"{VendorSpecificHex.Trim()}\" isn't valid hex. Use pairs of hex digits, like 01 04 C0 A8 64 0A.");
        }
        foreach (var custom in CustomOptions)
        {
            if (ReservedOptionReason(custom.Code) is { } why) { errors.Add($"Option {custom.Code} {why}."); continue; }
            if (extra.ContainsKey(custom.Code)) { errors.Add($"Option {custom.Code} is listed more than once."); continue; }
            if (CustomBytes(custom, errors) is { } bytes) extra[custom.Code] = bytes;
        }

        string? domain = null;
        if (!string.IsNullOrWhiteSpace(DomainName))
        {
            domain = DomainName.Trim();
            Text(domain, "Domain name", errors);
        }
        if (LeaseMinutes <= 0) errors.Add("Lease time must be a whole number of minutes.");
        if (errors.Count > 0) return (null, errors);

        var config = new DhcpServerConfig
        {
            ServerIp = server!,
            PrefixLength = PrefixLength,
            PoolStart = start!,
            PoolEnd = end!,
            LeaseTime = TimeSpan.FromMinutes(LeaseMinutes),
            Router = router,
            DnsServers = dns,
            DomainName = domain,
            ExtraOptions = extra,
            AlwaysBroadcast = AlwaysBroadcast,
        };
        var problems = config.Validate();
        return problems.Count > 0 ? (null, problems) : (config, []);
    }

    /// <summary>One line for the start button area, like "192.168.100.1/24, pool .100 to .199".</summary>
    public string Summary => $"{ServerIp}/{PrefixLength}, pool {PoolStart} to {PoolEnd}";

    private static IPAddress? Address(string? text, string label, List<string> errors)
    {
        if (IPv4.TryParse(text?.Trim(), out var ip)) return ip;
        errors.Add($"{label} \"{text?.Trim()}\" isn't a valid IPv4 address.");
        return null;
    }

    private static List<IPAddress> Addresses(string? text, string label, List<string> errors)
    {
        var list = new List<IPAddress>();
        foreach (var part in (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPv4.TryParse(part, out var ip)) list.Add(ip);
            else errors.Add($"{label} \"{part}\" isn't a valid IPv4 address.");
        }
        if (list.Count > 63) errors.Add($"Too many {label} addresses (63 at most).");
        return list;
    }

    private static byte[] ToBytes(IEnumerable<IPAddress> addresses) => addresses.SelectMany(a => a.GetAddressBytes()).ToArray();

    private static byte[] Text(string text, string label, List<string> errors)
    {
        var trimmed = text.Trim();
        if (trimmed.Any(c => c < 0x20 || c > 0x7E))
        {
            errors.Add($"{label} can only use plain letters, digits and punctuation.");
            return [];
        }
        return Fit(Encoding.ASCII.GetBytes(trimmed), label, errors);
    }

    private static byte[] Fit(byte[] data, string label, List<string> errors)
    {
        if (data.Length == 0) errors.Add($"{label} is empty.");
        else if (data.Length > 255) errors.Add($"{label} is too long ({data.Length} bytes; a DHCP option holds 255 at most).");
        return data;
    }

    private static byte[]? CustomBytes(CustomOption option, List<string> errors)
    {
        var label = $"Option {option.Code}";
        switch (option.Format)
        {
            case CustomOptionFormat.Text:
                return Text(option.Value ?? "", label, errors);
            case CustomOptionFormat.Addresses:
                var list = Addresses(option.Value, label, errors);
                if (list.Count == 0 && string.IsNullOrWhiteSpace(option.Value)) errors.Add($"{label} is empty.");
                return ToBytes(list);
            default:
                if (TryParseHex(option.Value, out var bytes)) return Fit(bytes, label, errors);
                errors.Add($"{label} \"{option.Value?.Trim()}\" isn't valid hex. Use pairs of hex digits, like 01 04 C0 A8 64 0A.");
                return null;
        }
    }

    /// <summary>Accepts "0104C0A8", "01 04 c0 a8", "01:04:C0:A8" and "0x01,0x04".</summary>
    public static bool TryParseHex(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = new StringBuilder();
        foreach (var token in text.Split([' ', ':', '-', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            // Separated single digits like "1 4" mean 01 04.
            if (t.Length == 1) t = "0" + t;
            clean.Append(t);
        }
        var hex = clean.ToString();
        if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return false;
        bytes = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>Formats bytes the way the form shows them: "01 04 C0 A8".</summary>
    public static string ToHex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
