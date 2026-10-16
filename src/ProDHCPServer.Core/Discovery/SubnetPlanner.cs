using System.Net;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Profiles;

namespace ProDHCPServer.Core.Discovery;

/// <summary>A /24 to scan, and why it's on the list.</summary>
public sealed record SweepTarget(IPAddress Network, int PrefixLength, string Reason)
{
    public string Cidr => IPv4.ToCidr(Network, PrefixLength);
    /// <summary>"192.168.1.x" for a /24.</summary>
    public string Short => PrefixLength == 24 ? $"{string.Join('.', Network.ToString().Split('.')[..3])}.x" : Cidr;
}

public sealed record SkippedSubnet(string Subnet, string Reason);

/// <summary>Decides which subnets to scan and which temporary address the laptop takes on each.</summary>
public static class SubnetPlanner
{
    public static bool IsReachable(IPAddress ip, IEnumerable<AddressAssignment> local) =>
        local.Any(a => IPv4.SameSubnet(ip, a.Address, a.PrefixLength));

    /// <summary>The subnet size assumed for a device whose mask we don't know: /16 for 169.254.x.x, else /24.</summary>
    public static int ReachPrefix(IPAddress ip) => IPv4.IsApipa(ip) ? 16 : 24;

    /// <summary>
    /// Addresses to try for the laptop near <paramref name="near"/>, best first: .249 down to .200, then
    /// .199 down to .2, in the same /24 as <paramref name="near"/>. Never a documented factory address or
    /// anything in <paramref name="avoid"/>.
    /// </summary>
    public static IEnumerable<IPAddress> HelperCandidates(IPAddress near, IReadOnlySet<uint> avoid)
    {
        uint baseAddr = IPv4.ToUInt32(near) & 0xFFFFFF00;
        // 169.254.0.x and 169.254.255.x are reserved (RFC 3927).
        if (IPv4.IsApipa(near) && (baseAddr & 0xFF00) is 0 or 0xFF00) baseAddr = 0xA9FE0100;
        var factory = ProfilePresets.FactoryAddresses.Select(IPv4.ToUInt32).ToHashSet();
        foreach (uint host in Enumerable.Range(200, 50).Reverse().Concat(Enumerable.Range(2, 198).Reverse()).Select(h => (uint)h))
        {
            uint candidate = baseAddr | host;
            if (!avoid.Contains(candidate) && !factory.Contains(candidate)) yield return IPv4.FromUInt32(candidate);
        }
    }

    /// <summary>
    /// The subnets worth scanning: the laptop's own, the brand presets' factory subnets, and any subnet a
    /// device has already been seen on. Subnets another connected adapter (like Wi-Fi) uses are skipped,
    /// because a temporary address there would confuse Windows' routing.
    /// </summary>
    public static (IReadOnlyList<SweepTarget> Targets, IReadOnlyList<SkippedSubnet> Skipped) SweepTargets(
        IEnumerable<AddressAssignment> local, IEnumerable<DiscoveredDevice> found, IEnumerable<AdapterInfo> otherAdapters)
    {
        var wanted = new List<(uint Network, string Reason)>();
        void Want(IPAddress ip, string reason)
        {
            if (IPv4.IsApipa(ip) || !DiscoveryTable.IsDeviceAddress(ip)) return;
            uint net = IPv4.ToUInt32(ip) & 0xFFFFFF00;
            if (wanted.All(w => w.Network != net)) wanted.Add((net, reason));
        }

        foreach (var a in local) Want(a.Address, "this laptop's subnet");
        foreach (var d in found) Want(d.Ip, $"{d.Ip} was seen there");
        foreach (var p in ProfilePresets.All.Where(p => p.Sources.Count > 0))
            if (IPv4.TryParse(p.ServerIp, out var ip)) Want(ip, $"factory defaults ({p.Name})");

        var others = otherAdapters.Where(a => a.MediaConnected).ToList();
        var targets = new List<SweepTarget>();
        var skipped = new List<SkippedSubnet>();
        foreach (var (net, reason) in wanted)
        {
            var target = new SweepTarget(IPv4.FromUInt32(net), 24, reason);
            var clash = others.FirstOrDefault(o => o.Addresses.Any(a => IPv4.SubnetsOverlap(a.Address, a.PrefixLength, target.Network, 24)));
            if (clash is not null) skipped.Add(new SkippedSubnet(target.Short, $"\"{clash.Name}\" is on it ({clash.AddressSummary})"));
            else targets.Add(target);
        }
        return (targets, skipped);
    }
}

public enum HelperState
{
    /// <summary>Windows is still checking the address isn't in use.</summary>
    Pending,
    Ready,
    /// <summary>Another device already has it.</summary>
    Duplicate,
}

/// <summary>Adds and removes the laptop's temporary addresses. The Windows side goes through the adapter backup session.</summary>
public interface IHelperAddressHost
{
    IReadOnlyList<AddressAssignment> CurrentAddresses();
    void Add(IPAddress address, int prefixLength);
    void Remove(IPAddress address);
    HelperState State(IPAddress address);
}

/// <summary>Asks one address for its MAC (an ARP request). Null when nothing answers.</summary>
public interface IArpProber
{
    Task<MacAddress?> ProbeAsync(IPAddress target, IPAddress source, CancellationToken ct);
}

public sealed record HelperResult(IPAddress? Address, int PrefixLength, string? Problem, bool AlreadyReachable = false);

public sealed record SweepSummary(IReadOnlyList<string> Lines, int Found);

/// <summary>Temporary addresses on other subnets: to reach one device, or to scan a subnet for static IPs.</summary>
public sealed class SubnetHelper(IHelperAddressHost host, IArpProber prober, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(8);
    public const int MaxAttempts = 3;
    public const int Parallel = 32;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>The delay between checks of a new address; tests set it to zero.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gives the laptop an address on <paramref name="target"/>'s subnet and keeps it until Stop.
    /// Tries another address if Windows finds the first one already in use.
    /// </summary>
    public async Task<HelperResult> ReachAsync(IPAddress target, IReadOnlySet<uint> avoid, CancellationToken ct)
    {
        if (SubnetPlanner.IsReachable(target, host.CurrentAddresses())) return new HelperResult(null, 0, null, AlreadyReachable: true);
        var skip = new HashSet<uint>(avoid) { IPv4.ToUInt32(target) };
        return await AddHelperAsync(target, SubnetPlanner.ReachPrefix(target), skip, ct);
    }

    private async Task<HelperResult> AddHelperAsync(IPAddress near, int prefix, HashSet<uint> avoid, CancellationToken ct)
    {
        string? last = null;
        foreach (var candidate in SubnetPlanner.HelperCandidates(near, avoid).Take(MaxAttempts))
        {
            ct.ThrowIfCancellationRequested();
            host.Add(candidate, prefix);
            var started = _now();
            var state = HelperState.Pending;
            while (state == HelperState.Pending && _now() - started < ReadyTimeout)
            {
                await Task.Delay(PollInterval, ct);
                state = host.State(candidate);
            }
            if (state == HelperState.Ready) return new HelperResult(candidate, prefix, null);
            host.Remove(candidate);
            avoid.Add(IPv4.ToUInt32(candidate));
            last = state == HelperState.Duplicate
                ? $"{candidate} is already used by another device"
                : $"Windows didn't bring up {candidate} within {ReadyTimeout.TotalSeconds:0} s (is the cable connected?)";
            if (state != HelperState.Duplicate) break;
        }
        return new HelperResult(null, prefix, last ?? "No free address was found on that subnet.");
    }

    /// <summary>
    /// ARPs every address of each /24 in turn. Where the laptop has no address on a subnet it takes a temporary
    /// one for the scan and removes it afterwards. Every answer is reported through <paramref name="found"/>.
    /// </summary>
    public async Task<SweepSummary> SweepAsync(IReadOnlyList<SweepTarget> targets, IReadOnlySet<uint> avoid,
        Action<string> progress, Action<Sighting> found, CancellationToken ct)
    {
        var lines = new List<string>();
        int total = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[i];
            progress($"Scanning {target.Short} ({i + 1} of {targets.Count})…");
            var local = host.CurrentAddresses().FirstOrDefault(a => IPv4.SameSubnet(a.Address, target.Network, Math.Max(a.PrefixLength, 24)));
            IPAddress? helper = null;
            IPAddress source;
            if (local is not null) source = local.Address;
            else
            {
                var added = await AddHelperAsync(target.Network, 24, [.. avoid], ct);
                if (added.Address is null)
                {
                    lines.Add($"{target.Short}: skipped. {added.Problem}.");
                    continue;
                }
                helper = source = added.Address;
            }

            int count = 0;
            try
            {
                uint net = IPv4.ToUInt32(target.Network);
                var queue = new Queue<IPAddress>(Enumerable.Range(1, 254).Select(h => IPv4.FromUInt32(net | (uint)h)).Where(ip => !ip.Equals(source)));
                async Task Worker()
                {
                    while (true)
                    {
                        IPAddress next;
                        lock (queue) { if (queue.Count == 0) return; next = queue.Dequeue(); }
                        ct.ThrowIfCancellationRequested();
                        if (await prober.ProbeAsync(next, source, ct) is { } mac)
                        {
                            Interlocked.Increment(ref count);
                            found(new Sighting(_now(), DiscoverySource.Sweep, next) { Mac = mac, Detail = $"Answered ARP during the scan of {target.Short}" });
                        }
                    }
                }
                await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => Worker()));
            }
            finally
            {
                if (helper is not null) host.Remove(helper);
            }
            total += count;
            var via = helper is null ? $"from {source}" : $"using {helper} for the scan, now removed";
            lines.Add($"{target.Short}: {(count == 1 ? "1 device" : $"{count} devices")} answered ({via}).");
        }
        return new SweepSummary(lines, total);
    }
}
