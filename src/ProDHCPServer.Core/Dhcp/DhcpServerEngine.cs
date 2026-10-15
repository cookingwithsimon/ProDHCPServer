using System.Net;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Dhcp;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public enum ReplyKind
{
    /// <summary>255.255.255.255:68 out of the serving adapter.</summary>
    Broadcast,
    /// <summary>Unicast to yiaddr:68; the transport must first add a static ARP entry for the client MAC.</summary>
    UnicastToHardwareAddress,
    /// <summary>Unicast to ciaddr:68; the client already has a working address.</summary>
    UnicastToClientIp,
    /// <summary>Unicast to giaddr:67, a relay agent.</summary>
    Relay,
}

public sealed record ReplyDestination(ReplyKind Kind, IPAddress Address, int Port, MacAddress ClientMac);

public sealed record DhcpReply(DhcpPacket Packet, ReplyDestination Destination);

public enum DhcpEventKind
{
    Received,
    Sent,
    Ignored,
    LeaseOffered,
    LeaseBound,
    LeaseRenewed,
    LeaseReleased,
    LeaseDeclined,
    LeaseExpired,
    OtherServerChosen,
    PoolExhausted,
    BroadcastFallback,
}

public sealed record DhcpEngineEvent(
    DateTimeOffset Time,
    DhcpEventKind Kind,
    MacAddress Mac,
    DhcpMessageType? MessageType,
    IPAddress? Ip,
    string Message,
    string? Detail = null);

/// <summary>
/// The RFC 2131 server state machine. It has no I/O: the transport feeds it parsed requests
/// and sends whatever reply it returns to the destination it names. Thread-safe.
/// </summary>
public sealed class DhcpServerEngine
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<MacAddress, Lease> _leases = [];
    private readonly Dictionary<uint, DateTimeOffset> _declined = [];

    public DhcpServerEngine(DhcpServerConfig config, IClock? clock = null)
    {
        var errors = config.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(config));
        Config = config;
        _clock = clock ?? SystemClock.Instance;
    }

    public DhcpServerConfig Config { get; }

    public event Action<DhcpEngineEvent>? EventRaised;

    public IReadOnlyList<Lease> GetLeases()
    {
        lock (_gate) return _leases.Values.Select(l => l.Clone()).OrderBy(l => IPv4.ToUInt32(l.Ip)).ToList();
    }

    /// <summary>Addresses currently quarantined because a device declined them (likely an IP conflict).</summary>
    public IReadOnlyList<IPAddress> GetDeclinedAddresses()
    {
        lock (_gate)
        {
            var now = _clock.Now;
            return _declined.Where(d => d.Value > now).Select(d => IPv4.FromUInt32(d.Key)).ToList();
        }
    }

    /// <summary>Expires offers and leases whose time is up. Call about once a second.</summary>
    public void Tick()
    {
        var events = new List<DhcpEngineEvent>();
        lock (_gate)
        {
            var now = _clock.Now;
            foreach (var lease in _leases.Values)
            {
                if (lease.State is LeaseState.Offered or LeaseState.Bound && lease.Expires <= now)
                {
                    var was = lease.State;
                    lease.State = LeaseState.Expired;
                    events.Add(Event(DhcpEventKind.LeaseExpired, lease.Mac, null, lease.Ip,
                        was == LeaseState.Offered
                            ? $"Offer of {lease.Ip} to {lease.Mac} timed out: the device never sent a REQUEST for it."
                            : $"Lease {lease.Ip} for {lease.Mac} expired without being renewed."));
                }
            }
            foreach (var key in _declined.Where(d => d.Value <= now).Select(d => d.Key).ToList())
                _declined.Remove(key);
        }
        Raise(events);
    }

    public DhcpReply? Handle(DhcpPacket request)
    {
        var events = new List<DhcpEngineEvent>();
        DhcpReply? reply;
        lock (_gate) reply = HandleLocked(request, events);
        Raise(events);
        return reply;
    }

    private DhcpReply? HandleLocked(DhcpPacket req, List<DhcpEngineEvent> events)
    {
        var mac = req.HardwareLength >= 6 ? req.ClientMac : MacAddress.Empty;
        var type = req.MessageType;

        events.Add(Event(DhcpEventKind.Received, mac, type, null, req.Summary(), req.Describe()));

        if (req.Op != BootOp.Request)
            return Ignore(events, mac, type, "Ignored a BOOTREPLY: another DHCP server or relay is talking on this link.");
        if (req.HardwareType != 1 || req.HardwareLength != 6)
            return Ignore(events, mac, type, $"Ignored a request with hardware type {req.HardwareType}/length {req.HardwareLength}; only Ethernet is supported.");
        if (mac.IsBroadcast || mac == MacAddress.Empty)
            return Ignore(events, mac, type, "Ignored a request with an invalid client MAC address.");

        if (type is null)
            return HandleBootp(req, mac, events);

        return type switch
        {
            DhcpMessageType.Discover => HandleDiscover(req, mac, events),
            DhcpMessageType.Request => HandleRequest(req, mac, events),
            DhcpMessageType.Decline => HandleDecline(req, mac, events),
            DhcpMessageType.Release => HandleRelease(req, mac, events),
            DhcpMessageType.Inform => HandleInform(req, mac, events),
            _ => Ignore(events, mac, type, $"Ignored a {type} message, which clients do not send to servers."),
        };
    }

    private DhcpReply? HandleDiscover(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        var now = _clock.Now;
        _leases.TryGetValue(mac, out var existing);

        // A second DISCOVER while our unicast OFFER is still outstanding means the client never saw it.
        if (existing is { State: LeaseState.Offered, LastOfferWasUnicast: true, ForceBroadcast: false } && existing.Expires > now)
        {
            existing.ForceBroadcast = true;
            events.Add(Event(DhcpEventKind.BroadcastFallback, mac, DhcpMessageType.Discover, existing.Ip,
                $"{mac} did not answer a unicast OFFER, so replies to it will be broadcast from now on."));
        }

        var ip = ChooseAddress(mac, req.RequestedIp, now);
        if (ip is null)
        {
            events.Add(Event(DhcpEventKind.PoolExhausted, mac, DhcpMessageType.Discover, null,
                $"No free address for {mac}: all {Config.PoolSize} pool addresses are in use or declined. Widen the pool or shorten the lease time."));
            return null;
        }

        var lease = Upsert(mac, ip, req, now);
        lease.State = LeaseState.Offered;
        lease.Expires = now + Config.OfferHoldTime;

        var offer = BuildReply(req, DhcpMessageType.Offer, ip, includeLease: true);
        var dest = ChooseDestination(req, offer, lease.ForceBroadcast);
        lease.LastOfferWasUnicast = dest.Kind == ReplyKind.UnicastToHardwareAddress;
        events.Add(Event(DhcpEventKind.LeaseOffered, mac, DhcpMessageType.Offer, ip, $"Offering {ip} to {Name(lease)}."));
        return Sent(events, offer, dest);
    }

    private DhcpReply? HandleRequest(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        var now = _clock.Now;
        var serverId = req.ServerIdentifier;
        var requested = req.RequestedIp;
        _leases.TryGetValue(mac, out var lease);

        if (serverId is not null)
        {
            // SELECTING: the client is answering an OFFER.
            if (!serverId.Equals(Config.ServerIp))
            {
                if (lease is { State: LeaseState.Offered }) { lease.State = LeaseState.Expired; lease.Expires = now; }
                events.Add(Event(DhcpEventKind.OtherServerChosen, mac, DhcpMessageType.Request, requested,
                    $"{mac} accepted an offer from another DHCP server at {serverId}. There is a second DHCP server on this link."));
                return null;
            }
            if (requested is null)
                return Nak(req, mac, events, "REQUEST named this server but did not say which address it wants.");
            if (lease is null || !lease.Ip.Equals(requested) || !IsAvailableFor(mac, requested, now))
                return Nak(req, mac, events, $"{mac} asked for {requested}, which this server did not offer to it.");
            return Ack(req, lease, now, events, renewed: false);
        }

        if (IPv4.IsZero(req.ClientIp))
        {
            // INIT-REBOOT: the client is checking an address it remembers.
            if (requested is null)
                return Ignore(events, mac, DhcpMessageType.Request, "REQUEST without a server identifier, requested IP or ciaddr; ignored as malformed.");
            if (!Config.InSubnet(requested))
                return Nak(req, mac, events, $"{mac} wants to keep {requested} from a different network; told it to start over.");
            if (lease is not null && lease.Ip.Equals(requested) && IsAvailableFor(mac, requested, now))
                return Ack(req, lease, now, events, renewed: lease.State == LeaseState.Bound);
            if (Config.InPool(requested) && IsAvailableFor(mac, requested, now))
                return Ack(req, Upsert(mac, requested, req, now), now, events, renewed: false);
            return Nak(req, mac, events, $"{mac} wants to keep {requested}, which is not available; told it to start over.");
        }

        // RENEWING (unicast) or REBINDING (broadcast): ciaddr is the address in use.
        var ciaddr = req.ClientIp;
        if (!Config.InSubnet(ciaddr))
            return Nak(req, mac, events, $"{mac} tried to renew {ciaddr}, which is not on this network.");
        if (lease is not null && lease.Ip.Equals(ciaddr) && IsAvailableFor(mac, ciaddr, now))
            return Ack(req, lease, now, events, renewed: true);
        if (Config.InPool(ciaddr) && IsAvailableFor(mac, ciaddr, now))
            return Ack(req, Upsert(mac, ciaddr, req, now), now, events, renewed: false);
        return Nak(req, mac, events, $"{mac} tried to renew {ciaddr}, which this server cannot give it.");
    }

    private DhcpReply? HandleDecline(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        if (req.ServerIdentifier is { } sid && !sid.Equals(Config.ServerIp))
            return Ignore(events, mac, DhcpMessageType.Decline, $"DECLINE addressed to another server ({sid}).");
        var ip = req.RequestedIp;
        if (ip is null)
            return Ignore(events, mac, DhcpMessageType.Decline, "DECLINE without the declined address; ignored.");

        var now = _clock.Now;
        _declined[IPv4.ToUInt32(ip)] = now + Config.DeclineQuarantine;
        if (_leases.TryGetValue(mac, out var lease) && lease.Ip.Equals(ip))
        {
            lease.State = LeaseState.Expired;
            lease.Expires = now;
        }
        events.Add(Event(DhcpEventKind.LeaseDeclined, mac, DhcpMessageType.Decline, ip,
            $"{mac} found {ip} already in use and declined it. Another device on the cable probably has {ip} as a static IP. That address is skipped for {Config.DeclineQuarantine.TotalMinutes:0} minutes."));
        return null;
    }

    private DhcpReply? HandleRelease(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        if (req.ServerIdentifier is { } sid && !sid.Equals(Config.ServerIp))
            return Ignore(events, mac, DhcpMessageType.Release, $"RELEASE addressed to another server ({sid}).");
        if (_leases.TryGetValue(mac, out var lease) && lease.Ip.Equals(req.ClientIp))
        {
            lease.State = LeaseState.Released;
            lease.Expires = _clock.Now;
            events.Add(Event(DhcpEventKind.LeaseReleased, mac, DhcpMessageType.Release, lease.Ip, $"{Name(lease)} released {lease.Ip}."));
            return null;
        }
        return Ignore(events, mac, DhcpMessageType.Release, $"RELEASE of {req.ClientIp} from {mac}, which holds no lease for it here.");
    }

    private DhcpReply? HandleInform(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        if (IPv4.IsZero(req.ClientIp))
            return Ignore(events, mac, DhcpMessageType.Inform, "INFORM without ciaddr; ignored.");
        // RFC 2131 4.3.5: ACK with configuration only, no yiaddr and no lease time, unicast to ciaddr.
        var ack = BuildReply(req, DhcpMessageType.Ack, IPAddress.Any, includeLease: false);
        return Sent(events, ack, ChooseDestination(req, ack, forceBroadcast: false));
    }

    private DhcpReply? HandleBootp(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events)
    {
        // Plain BOOTP (no message type): still common on older AV gear. Allocate and answer at once.
        var now = _clock.Now;
        var ip = ChooseAddress(mac, null, now);
        if (ip is null)
        {
            events.Add(Event(DhcpEventKind.PoolExhausted, mac, null, null, $"No free address for BOOTP client {mac}."));
            return null;
        }
        var lease = Upsert(mac, ip, req, now);
        lease.IsBootp = true;
        lease.State = LeaseState.Bound;
        lease.BoundAt ??= now;
        lease.Expires = now + Config.LeaseTime;
        var reply = BuildReply(req, null, ip, includeLease: false);
        reply.HasMagicCookie = req.HasMagicCookie;
        events.Add(Event(DhcpEventKind.LeaseBound, mac, null, ip, $"{Name(lease)} got {ip} (BOOTP)."));
        return Sent(events, reply, ChooseDestination(req, reply, lease.ForceBroadcast));
    }

    private DhcpReply Ack(DhcpPacket req, Lease lease, DateTimeOffset now, List<DhcpEngineEvent> events, bool renewed)
    {
        Touch(lease, req, now);
        bool wasBound = lease.State == LeaseState.Bound && lease.Expires > now;
        lease.State = LeaseState.Bound;
        lease.Expires = now + Config.LeaseTime;
        if (!wasBound) lease.BoundAt = now;
        var ack = BuildReply(req, DhcpMessageType.Ack, lease.Ip, includeLease: true);
        events.Add(Event(renewed && wasBound ? DhcpEventKind.LeaseRenewed : DhcpEventKind.LeaseBound, lease.Mac, DhcpMessageType.Ack, lease.Ip,
            renewed && wasBound ? $"{Name(lease)} renewed {lease.Ip}." : $"{Name(lease)} is now using {lease.Ip}."));
        return Sent(events, ack, ChooseDestination(req, ack, lease.ForceBroadcast));
    }

    private DhcpReply Nak(DhcpPacket req, MacAddress mac, List<DhcpEngineEvent> events, string reason)
    {
        var nak = BuildReply(req, DhcpMessageType.Nak, IPAddress.Any, includeLease: false, configOptions: false);
        nak.SetOption(DhcpOption.FromString(DhcpOptionCode.Message, Truncate(reason, 200)));
        events.Add(Event(DhcpEventKind.Sent, mac, DhcpMessageType.Nak, req.RequestedIp ?? req.ClientIp, $"NAK: {reason}"));
        var dest = ChooseDestination(req, nak, forceBroadcast: false);
        return new DhcpReply(nak, dest);
    }

    private DhcpReply? Ignore(List<DhcpEngineEvent> events, MacAddress mac, DhcpMessageType? type, string reason)
    {
        events.Add(Event(DhcpEventKind.Ignored, mac, type, null, reason));
        return null;
    }

    private DhcpReply Sent(List<DhcpEngineEvent> events, DhcpPacket packet, ReplyDestination dest)
    {
        events.Add(Event(DhcpEventKind.Sent, dest.ClientMac, packet.MessageType, packet.YourIp,
            $"{packet.Summary()} via {Describe(dest)}", packet.Describe()));
        return new DhcpReply(packet, dest);
    }

    /// <summary>RFC 2131 section 4.1, "constructing and sending DHCP messages".</summary>
    public ReplyDestination ChooseDestination(DhcpPacket request, DhcpPacket reply, bool forceBroadcast)
    {
        var mac = request.ClientMac;
        if (!IPv4.IsZero(request.RelayIp))
        {
            if (reply.MessageType == DhcpMessageType.Nak) reply.IsBroadcastFlagSet = true;
            return new(ReplyKind.Relay, request.RelayIp, DhcpPorts.Server, mac);
        }
        if (reply.MessageType == DhcpMessageType.Nak)
            return new(ReplyKind.Broadcast, IPAddress.Broadcast, DhcpPorts.Client, mac);
        if (!IPv4.IsZero(request.ClientIp))
            return new(ReplyKind.UnicastToClientIp, request.ClientIp, DhcpPorts.Client, mac);
        if (request.IsBroadcastFlagSet || forceBroadcast || Config.AlwaysBroadcast || IPv4.IsZero(reply.YourIp))
            return new(ReplyKind.Broadcast, IPAddress.Broadcast, DhcpPorts.Client, mac);
        return new(ReplyKind.UnicastToHardwareAddress, reply.YourIp, DhcpPorts.Client, mac);
    }

    private DhcpPacket BuildReply(DhcpPacket req, DhcpMessageType? type, IPAddress yiaddr, bool includeLease, bool configOptions = true)
    {
        var reply = new DhcpPacket
        {
            Op = BootOp.Reply,
            HardwareType = req.HardwareType,
            HardwareLength = req.HardwareLength,
            TransactionId = req.TransactionId,
            Flags = req.Flags,
            ClientIp = type is DhcpMessageType.Ack or DhcpMessageType.Inform && !IPv4.IsZero(req.ClientIp) ? req.ClientIp : IPAddress.Any,
            YourIp = yiaddr,
            NextServerIp = type == DhcpMessageType.Nak ? IPAddress.Any : Config.ServerIp,
            RelayIp = req.RelayIp,
            ClientHardwareAddress = (byte[])req.ClientHardwareAddress.Clone(),
        };

        if (type is { } t) reply.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)t));
        if (type is not null) reply.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.ServerIdentifier, Config.ServerIp));
        if (includeLease && type is not null)
        {
            uint secs = (uint)Config.LeaseTime.TotalSeconds;
            reply.Options.Add(DhcpOption.FromUInt32(DhcpOptionCode.LeaseTime, secs));
            reply.Options.Add(DhcpOption.FromUInt32(DhcpOptionCode.RenewalTime, secs / 2));
            reply.Options.Add(DhcpOption.FromUInt32(DhcpOptionCode.RebindingTime, (uint)(secs * 0.875)));
        }
        if (configOptions)
        {
            reply.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.SubnetMask, Config.SubnetMask));
            if (Config.Router is { } router) reply.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.Router, router));
            if (Config.DnsServers.Count > 0) reply.Options.Add(DhcpOption.FromAddresses(DhcpOptionCode.DnsServers, Config.DnsServers));
            if (!string.IsNullOrWhiteSpace(Config.DomainName)) reply.Options.Add(DhcpOption.FromString(DhcpOptionCode.DomainName, Config.DomainName));
            reply.Options.Add(DhcpOption.FromAddress(DhcpOptionCode.BroadcastAddress, Config.SubnetBroadcast));
            foreach (var (code, data) in Config.ExtraOptions.OrderBy(kv => kv.Key))
            {
                if (code is DhcpOptionCode.Pad or DhcpOptionCode.End or DhcpOptionCode.MessageType or DhcpOptionCode.ServerIdentifier) continue;
                reply.SetOption(new DhcpOption(code, data));
            }
        }
        // RFC 6842: echo the client identifier.
        if (req.ClientId is { } clientId && type is not null) reply.SetOption(new DhcpOption(DhcpOptionCode.ClientId, clientId));
        return reply;
    }

    private IPAddress? ChooseAddress(MacAddress mac, IPAddress? requested, DateTimeOffset now)
    {
        // 1. The address this client had before (sticky), if still usable.
        if (_leases.TryGetValue(mac, out var lease) && Config.InPool(lease.Ip) && IsAvailableFor(mac, lease.Ip, now))
            return lease.Ip;
        // 2. The address the client asked for.
        if (requested is not null && Config.InPool(requested) && IsAvailableFor(mac, requested, now))
            return requested;
        // 3. The lowest address nobody has ever had, then the longest-unused address of a lapsed lease.
        uint start = IPv4.ToUInt32(Config.PoolStart), end = IPv4.ToUInt32(Config.PoolEnd);
        var everUsed = _leases.Values.Select(l => IPv4.ToUInt32(l.Ip)).ToHashSet();
        for (uint v = start; v <= end && v != 0; v++)
        {
            if (everUsed.Contains(v)) continue;
            var ip = IPv4.FromUInt32(v);
            if (IsAvailableFor(mac, ip, now)) return ip;
        }
        return _leases.Values
            .Where(l => l.Mac != mac && !l.IsActive(now) && Config.InPool(l.Ip) && IsAvailableFor(mac, l.Ip, now))
            .OrderBy(l => l.Expires)
            .Select(l => l.Ip)
            .FirstOrDefault();
    }

    private bool IsAvailableFor(MacAddress mac, IPAddress ip, DateTimeOffset now)
    {
        uint v = IPv4.ToUInt32(ip);
        if (ip.Equals(Config.ServerIp) || ip.Equals(Config.Router)) return false;
        if (!Config.InSubnet(ip)) return false;
        if (v == IPv4.ToUInt32(IPv4.NetworkAddress(Config.ServerIp, Config.PrefixLength)) || ip.Equals(Config.SubnetBroadcast)) return false;
        if (_declined.TryGetValue(v, out var until) && until > now) return false;
        return !_leases.Values.Any(l => l.Mac != mac && l.Ip.Equals(ip) && l.IsActive(now));
    }

    private Lease Upsert(MacAddress mac, IPAddress ip, DhcpPacket req, DateTimeOffset now)
    {
        if (!_leases.TryGetValue(mac, out var lease))
        {
            lease = new Lease { Mac = mac, Ip = ip, FirstSeen = now, LastSeen = now, Expires = now };
            _leases[mac] = lease;
        }
        else if (!lease.Ip.Equals(ip))
        {
            lease.Ip = ip;
            lease.BoundAt = null;
        }
        Touch(lease, req, now);
        return lease;
    }

    private static void Touch(Lease lease, DhcpPacket req, DateTimeOffset now)
    {
        lease.LastSeen = now;
        if (req.HostName is { Length: > 0 } host) lease.HostName = host;
        if (req.VendorClass is { Length: > 0 } vendor) lease.VendorClass = vendor;
        if (req.ClientId is { } id) lease.ClientId = id;
    }

    private static string Name(Lease lease) => lease.HostName is { Length: > 0 } h ? $"{h} ({lease.Mac})" : lease.Mac.ToString();

    private static string Describe(ReplyDestination d) => d.Kind switch
    {
        ReplyKind.Broadcast => "broadcast",
        ReplyKind.UnicastToHardwareAddress => $"unicast to {d.Address} ({d.ClientMac})",
        ReplyKind.UnicastToClientIp => $"unicast to {d.Address}",
        ReplyKind.Relay => $"relay {d.Address}",
        _ => d.Kind.ToString(),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private DhcpEngineEvent Event(DhcpEventKind kind, MacAddress mac, DhcpMessageType? type, IPAddress? ip, string message, string? detail = null) =>
        new(_clock.Now, kind, mac, type, ip, message, detail);

    private void Raise(List<DhcpEngineEvent> events)
    {
        foreach (var e in events) EventRaised?.Invoke(e);
    }
}
