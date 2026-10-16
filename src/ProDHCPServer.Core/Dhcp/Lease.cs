using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Dhcp;

public enum LeaseState
{
    Offered,
    Bound,
    Released,
    Expired,
}

/// <summary>What the server knows about one client, keyed by MAC address.</summary>
public sealed class Lease
{
    public required MacAddress Mac { get; init; }
    public required IPAddress Ip { get; set; }
    public LeaseState State { get; set; }
    public string? HostName { get; set; }
    public string? VendorClass { get; set; }
    public byte[]? ClientId { get; set; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? BoundAt { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool IsBootp { get; set; }

    /// <summary>
    /// Set when this client re-sent DISCOVER after a unicast OFFER, which means it never saw the
    /// unicast reply even though it did not ask for broadcast. All later replies are broadcast.
    /// </summary>
    public bool ForceBroadcast { get; set; }

    internal bool LastOfferWasUnicast { get; set; }

    public bool IsActive(DateTimeOffset now) => State is LeaseState.Offered or LeaseState.Bound && Expires > now;

    public Lease Clone() => (Lease)MemberwiseClone();
}
