using System.Net;
using System.Text.Json.Serialization;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Adapters;

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Vpn,
    Virtual,
    Other,
}

public sealed record AddressAssignment(
    [property: JsonConverter(typeof(IPAddressJsonConverter))] IPAddress Address,
    int PrefixLength)
{
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>Live view of one network adapter.</summary>
public sealed record AdapterInfo
{
    public required string Id { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public MacAddress Mac { get; init; }
    public AdapterKind Kind { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool MediaConnected { get; init; }
    public long SpeedBitsPerSecond { get; init; }
    /// <summary>Null when Windows does not report it.</summary>
    public bool? FullDuplex { get; init; }
    public bool DhcpEnabled { get; init; }
    public IReadOnlyList<AddressAssignment> Addresses { get; init; } = [];
    public IReadOnlyList<IPAddress> Gateways { get; init; } = [];
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];
    public IPAddress? DhcpServer { get; init; }
    /// <summary>Addresses Windows found to be duplicated on the link (DAD failed).</summary>
    public IReadOnlyList<IPAddress> DuplicateAddresses { get; init; } = [];
    /// <summary>Interface byte counters since the adapter appeared; null when Windows doesn't report them.</summary>
    public long? BytesReceived { get; init; }
    public long? BytesSent { get; init; }
    public AdapterDriver? Driver { get; init; }

    public bool HasAddress(IPAddress ip) => Addresses.Any(a => a.Address.Equals(ip));
    public bool HasOnlyApipa => Addresses.Count > 0 && Addresses.All(a => IPv4.IsApipa(a.Address));

    public string SpeedText => SpeedBitsPerSecond switch
    {
        <= 0 => "unknown speed",
        >= 1_000_000_000 when SpeedBitsPerSecond % 1_000_000_000 == 0 => $"{SpeedBitsPerSecond / 1_000_000_000} Gbps",
        >= 1_000_000 => $"{SpeedBitsPerSecond / 1_000_000} Mbps",
        _ => $"{SpeedBitsPerSecond / 1000} kbps",
    };

    public string DuplexText => FullDuplex switch { true => "full duplex", false => "half duplex", null => "duplex unknown" };

    public string AddressSummary =>
        Addresses.Count == 0 ? "no IPv4 address" : string.Join(", ", Addresses.Select(a => a.ToString()));
}

/// <summary>The installed driver, as Windows records it. Any field may be missing.</summary>
public sealed record AdapterDriver(string? Provider, string? Version, DateOnly? Date, string? HardwareId)
{
    public override string ToString()
    {
        var parts = new[] { Provider, Version, Date?.ToString("yyyy-MM-dd") }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" ", parts);
    }
}

/// <summary>
/// The adapter's IPv4 configuration before we touched it. Everything needed to put it back.
/// </summary>
public sealed record AdapterSnapshot
{
    public required string AdapterId { get; init; }
    public required int InterfaceIndex { get; init; }
    public required string AdapterName { get; init; }
    public required bool DhcpEnabled { get; init; }
    /// <summary>Static addresses (empty when DHCP is on).</summary>
    public IReadOnlyList<AddressAssignment> StaticAddresses { get; init; } = [];
    [JsonConverter(typeof(IPAddressListJsonConverter))]
    public IReadOnlyList<IPAddress> Gateways { get; init; } = [];
    /// <summary>True when DNS servers come from DHCP rather than being typed in.</summary>
    public bool DnsFromDhcp { get; init; } = true;
    [JsonConverter(typeof(IPAddressListJsonConverter))]
    public IReadOnlyList<IPAddress> StaticDnsServers { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; }

    public string Describe()
    {
        if (DhcpEnabled)
            return DnsFromDhcp
                ? "Automatic (DHCP) address and DNS"
                : $"Automatic (DHCP) address, DNS {string.Join(", ", StaticDnsServers)}";
        var s = $"Static {string.Join(", ", StaticAddresses)}";
        if (Gateways.Count > 0) s += $", gateway {string.Join(", ", Gateways)}";
        if (!DnsFromDhcp && StaticDnsServers.Count > 0) s += $", DNS {string.Join(", ", StaticDnsServers)}";
        return s;
    }
}

public sealed record DisabledAdapter(string AdapterId, string Name);

/// <summary>Written to disk before any change, so a crash can always be undone on next launch.</summary>
public sealed record SessionRecord
{
    public required AdapterSnapshot Snapshot { get; init; }
    [JsonConverter(typeof(IPAddressJsonConverter))]
    public required IPAddress ServerIp { get; init; }
    public required int PrefixLength { get; init; }
    public IReadOnlyList<AddressAssignment> TemporaryAddresses { get; init; } = [];
    public IReadOnlyList<DisabledAdapter> DisabledAdapters { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public int ProcessId { get; init; }
    public string AppVersion { get; init; } = "";
}
