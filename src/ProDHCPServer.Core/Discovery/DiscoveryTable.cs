using System.Net;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Discovery;

/// <summary>How a device was found. Shown to the user as a short label.</summary>
public enum DiscoverySource
{
    /// <summary>It sent an ARP announcement or reply (needs Npcap).</summary>
    Arp,
    /// <summary>It checked its address with an ARP probe, as devices do at power-up (needs Npcap).</summary>
    ArpProbe,
    /// <summary>Windows' own ARP table: it has talked to this laptop.</summary>
    ArpTable,
    /// <summary>It sent a broadcast or multicast packet this laptop received.</summary>
    Traffic,
    Mdns,
    Ssdp,
    /// <summary>A manufacturer's own discovery protocol.</summary>
    Vendor,
    /// <summary>It answered the subnet scan.</summary>
    Sweep,
}

/// <summary>One observation of a device: at least its IP, plus whatever the packet said about it.</summary>
public sealed record Sighting(DateTimeOffset At, DiscoverySource Source, IPAddress Ip)
{
    public MacAddress? Mac { get; init; }
    public string? Name { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    /// <summary>A short label for what answered, like "Dante" or "PJLink" (shown after the source).</summary>
    public string? Label { get; init; }
    public Uri? WebPage { get; init; }
    public string? Detail { get; init; }
}

public sealed record DiscoveredDevice
{
    public required int Id { get; init; }
    public required IPAddress Ip { get; init; }
    public MacAddress? Mac { get; init; }
    public string? Name { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public Uri? WebPage { get; init; }
    /// <summary>Short labels of how it was found, like "mDNS", "ARP", "PJLink".</summary>
    public IReadOnlyList<string> FoundBy { get; init; } = [];
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    /// <summary>Recent raw details (service names, headers), newest last.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>The best name to show: its own name, then model, then manufacturer.</summary>
    public string Title => Name ?? Model ?? Manufacturer ?? "Unknown device";

    /// <summary>True when the laptop has an address on this device's subnet, so it can be reached.</summary>
    public bool IsReachable(IEnumerable<AddressAssignment> local) => SubnetPlanner.IsReachable(Ip, local);
}

public enum DiscoveryChange
{
    New,
    /// <summary>It now has a name, model or MAC it didn't have before.</summary>
    MoreDetail,
    /// <summary>Same MAC, different IP (for example it took a DHCP lease).</summary>
    IpChanged,
}

public sealed record DiscoveryUpdate(DiscoveryChange Change, DiscoveredDevice Device, IPAddress? PreviousIp = null);

/// <summary>
/// Merges sightings from every discovery method into one list of devices. A device is matched by
/// MAC when one is known, otherwise by IP. Thread-safe.
/// </summary>
public sealed class DiscoveryTable
{
    public const int MaxDevices = 256;
    private const int MaxDetails = 8;
    private readonly object _gate = new();
    private readonly List<DiscoveredDevice> _devices = [];
    private HashSet<uint> _own = [];
    private int _nextId = 1;
    private int _version;

    /// <summary>Bumped on every change, so the UI can skip redrawing when nothing happened.</summary>
    public int Version { get { lock (_gate) return _version; } }

    /// <summary>This laptop's own addresses, which are never listed as devices.</summary>
    public void SetOwnAddresses(IEnumerable<IPAddress> addresses)
    {
        lock (_gate) _own = addresses.Select(IPv4.ToUInt32).ToHashSet();
    }

    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_gate) return _devices.OrderBy(d => IPv4.ToUInt32(d.Ip)).ToList();
    }

    public void Clear()
    {
        lock (_gate) { _devices.Clear(); _version++; }
    }

    /// <summary>Whether an address is worth listing: a normal unicast address that isn't this laptop's.</summary>
    public static bool IsDeviceAddress(IPAddress ip)
    {
        uint v = IPv4.ToUInt32(ip);
        return v != 0 && v != 0xFFFFFFFF && (v >> 28) != 0xE && (v >> 28) != 0xF && !IPv4.IsLoopback(ip);
    }

    /// <summary>Adds a sighting. Returns what changed, or null if it told us nothing new.</summary>
    public DiscoveryUpdate? Add(Sighting s)
    {
        if (!IsDeviceAddress(s.Ip)) return null;
        var mac = s.Mac is { } m && m != MacAddress.Empty && !m.IsBroadcast ? m : (MacAddress?)null;
        lock (_gate)
        {
            if (_own.Contains(IPv4.ToUInt32(s.Ip))) return null;

            int index;
            if (mac is { } known)
            {
                index = _devices.FindIndex(d => d.Mac == known);
                // An entry for the same address with no MAC yet is this device too.
                int ipOnly = _devices.FindIndex(d => d.Mac is null && d.Ip.Equals(s.Ip));
                if (index < 0) index = ipOnly;
                else if (ipOnly >= 0)
                {
                    _devices[index] = Merge(_devices[index], _devices[ipOnly]);
                    _devices.RemoveAt(ipOnly);
                    if (ipOnly < index) index--;
                }
            }
            else index = _devices.FindIndex(d => d.Ip.Equals(s.Ip));

            if (index < 0)
            {
                if (_devices.Count >= MaxDevices) return null;
                var created = Apply(new DiscoveredDevice { Id = _nextId++, Ip = s.Ip, FirstSeen = s.At, LastSeen = s.At }, s, mac);
                _devices.Add(created);
                _version++;
                return new DiscoveryUpdate(DiscoveryChange.New, created);
            }

            var before = _devices[index];
            var after = Apply(before, s, mac);
            _devices[index] = after;
            if (!before.Ip.Equals(after.Ip)) { _version++; return new DiscoveryUpdate(DiscoveryChange.IpChanged, after, before.Ip); }
            bool more = before.Mac != after.Mac || before.Name != after.Name || before.Model != after.Model
                || before.Manufacturer != after.Manufacturer || before.WebPage != after.WebPage || before.FoundBy.Count != after.FoundBy.Count;
            if (more || before.Details.Count != after.Details.Count) _version++;
            return more ? new DiscoveryUpdate(DiscoveryChange.MoreDetail, after) : null;
        }
    }

    private static DiscoveredDevice Apply(DiscoveredDevice d, Sighting s, MacAddress? mac)
    {
        var label = s.Label ?? Label(s.Source);
        var details = d.Details;
        if (s.Detail is { Length: > 0 } detail && !details.Contains(detail)) details = [.. details.TakeLast(MaxDetails - 1), detail];
        return d with
        {
            Ip = s.Ip,
            Mac = mac ?? d.Mac,
            // A name the device gives itself beats one guessed from elsewhere; keep the first good one.
            Name = d.Name ?? Clean(s.Name),
            Model = d.Model ?? Clean(s.Model),
            Manufacturer = d.Manufacturer ?? Clean(s.Manufacturer),
            WebPage = d.WebPage ?? s.WebPage,
            FoundBy = d.FoundBy.Contains(label) ? d.FoundBy : [.. d.FoundBy, label],
            LastSeen = s.At > d.LastSeen ? s.At : d.LastSeen,
            Details = details,
        };
    }

    private static DiscoveredDevice Merge(DiscoveredDevice into, DiscoveredDevice from) => into with
    {
        Name = into.Name ?? from.Name,
        Model = into.Model ?? from.Model,
        Manufacturer = into.Manufacturer ?? from.Manufacturer,
        WebPage = into.WebPage ?? from.WebPage,
        FoundBy = into.FoundBy.Union(from.FoundBy).ToList(),
        FirstSeen = from.FirstSeen < into.FirstSeen ? from.FirstSeen : into.FirstSeen,
        Details = from.Details.Concat(into.Details).Distinct().TakeLast(MaxDetails).ToList(),
    };

    private static string? Clean(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        t = new string(t.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return t.Length > 80 ? t[..80] + "…" : t;
    }

    public static string Label(DiscoverySource source) => source switch
    {
        DiscoverySource.Arp or DiscoverySource.ArpProbe or DiscoverySource.ArpTable => "ARP",
        DiscoverySource.Traffic => "its traffic",
        DiscoverySource.Mdns => "mDNS",
        DiscoverySource.Ssdp => "UPnP",
        DiscoverySource.Sweep => "subnet scan",
        _ => "discovery",
    };
}
