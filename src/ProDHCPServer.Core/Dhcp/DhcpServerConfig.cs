using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Dhcp;

/// <summary>Everything the server hands out. Immutable; build a new one to change settings.</summary>
public sealed record DhcpServerConfig
{
    public required IPAddress ServerIp { get; init; }
    public required int PrefixLength { get; init; }
    public required IPAddress PoolStart { get; init; }
    public required IPAddress PoolEnd { get; init; }
    public TimeSpan LeaseTime { get; init; } = TimeSpan.FromHours(1);
    public IPAddress? Router { get; init; }
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];
    public string? DomainName { get; init; }

    /// <summary>Extra raw options (e.g. 66, 150, 43). They override built-in ones with the same code.</summary>
    public IReadOnlyDictionary<byte, byte[]> ExtraOptions { get; init; } = new Dictionary<byte, byte[]>();

    /// <summary>How long an offered address is held for a client that has not yet sent REQUEST.</summary>
    public TimeSpan OfferHoldTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long an address reported in DHCPDECLINE stays out of the pool.</summary>
    public TimeSpan DeclineQuarantine { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Broadcast every reply to clients without an address, whatever the broadcast flag says.</summary>
    public bool AlwaysBroadcast { get; init; }

    public IPAddress SubnetMask => IPv4.PrefixToMaskAddress(PrefixLength);
    public IPAddress SubnetBroadcast => IPv4.BroadcastAddress(ServerIp, PrefixLength);
    public int PoolSize => (int)Math.Max(0, (long)IPv4.ToUInt32(PoolEnd) - IPv4.ToUInt32(PoolStart) + 1);

    public static DhcpServerConfig Default => new()
    {
        ServerIp = IPv4.Parse("192.168.100.1"),
        PrefixLength = 24,
        PoolStart = IPv4.Parse("192.168.100.100"),
        PoolEnd = IPv4.Parse("192.168.100.199"),
        LeaseTime = TimeSpan.FromHours(1),
    };

    public bool InSubnet(IPAddress ip) => IPv4.SameSubnet(ip, ServerIp, PrefixLength);

    public bool InPool(IPAddress ip)
    {
        uint v = IPv4.ToUInt32(ip);
        return v >= IPv4.ToUInt32(PoolStart) && v <= IPv4.ToUInt32(PoolEnd);
    }

    /// <summary>Plain-English problems that would stop the server from working. Empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PrefixLength is < 8 or > 30)
            errors.Add($"Subnet /{PrefixLength} is not supported. Use a prefix between /8 and /30 (for example /24 = 255.255.255.0).");
        else
        {
            uint server = IPv4.ToUInt32(ServerIp);
            uint network = IPv4.ToUInt32(IPv4.NetworkAddress(ServerIp, PrefixLength));
            uint broadcast = IPv4.ToUInt32(SubnetBroadcast);
            if (server == network || server == broadcast)
                errors.Add($"Server IP {ServerIp} is the network or broadcast address of {IPv4.ToCidr(ServerIp, PrefixLength)}. Pick a host address such as {IPv4.FromUInt32(network + 1)}.");
            if (IPv4.IsApipa(ServerIp) || IPv4.IsLoopback(ServerIp) || IPv4.ToUInt32(ServerIp) >= 0xE0000000)
                errors.Add($"Server IP {ServerIp} is in a reserved range. Use a private range like 192.168.x.x or 10.x.x.x.");
            if (!InSubnet(PoolStart) || !InSubnet(PoolEnd))
                errors.Add($"The pool {PoolStart} to {PoolEnd} must be inside the server's subnet {IPv4.ToCidr(ServerIp, PrefixLength)}.");
            if (IPv4.ToUInt32(PoolStart) > IPv4.ToUInt32(PoolEnd))
                errors.Add($"The pool start {PoolStart} is after the pool end {PoolEnd}.");
            else
            {
                uint ps = IPv4.ToUInt32(PoolStart), pe = IPv4.ToUInt32(PoolEnd);
                if (ps <= network || pe >= broadcast)
                    errors.Add("The pool must not include the network or broadcast address.");
                if (InPool(ServerIp) && PoolSize == 1)
                    errors.Add("The pool only contains the server's own IP, so there is nothing to hand out.");
            }
            if (Router is { } r && !InSubnet(r))
                errors.Add($"The gateway {r} is outside the subnet {IPv4.ToCidr(ServerIp, PrefixLength)}; devices will not be able to reach it.");
        }
        if (LeaseTime < TimeSpan.FromSeconds(60))
            errors.Add("Lease time must be at least 60 seconds.");
        if (LeaseTime > TimeSpan.FromDays(365))
            errors.Add("Lease time must be at most 365 days.");
        return errors;
    }
}
