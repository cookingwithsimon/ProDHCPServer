using System.Net;
using System.Runtime.Versioning;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Discovery;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

/// <summary>
/// The static IP finder and device discovery for one serving session. Listens for broadcasts, mDNS, UPnP and
/// manufacturers' discovery packets, reads Windows' ARP table, asks the link every so often, and (with Npcap)
/// watches ARP, LLDP and CDP. Temporary addresses for reaching or scanning other subnets go through the
/// adapter backup session, so Stop, exit and a crash all remove them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiscoveryService : IAsyncDisposable, IHelperAddressHost
{
    private static readonly TimeSpan[] EarlyRounds = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12)];
    private static readonly TimeSpan RoundInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(3);
    private const int MaxLearnedTypes = 24;

    private readonly IAdapterControl _adapters;
    private readonly AdapterSessionManager _sessions;
    private readonly SessionLog _log;
    private readonly WindowsArp _arp = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = TimeSpan.FromSeconds(5),
        MaxResponseContentBufferSize = UpnpDescription.MaxBytes,
    };
    private readonly object _gate = new();
    private readonly HashSet<string> _learnedTypes = [];
    private readonly Dictionary<uint, Uri> _pendingDescriptions = [];
    private readonly HashSet<string> _fetched = [];
    private readonly List<DiscoverySocket> _vendorSockets = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _sweep;
    private RawIpSniffer? _sniffer;
    private DiscoverySocket? _mdns;
    private DiscoverySocket? _ssdp;
    private NpcapCapture? _npcap;
    private string? _adapterId;
    private string _adapterName = "";
    private int _interfaceIndex;
    private IReadOnlyList<AddressAssignment> _local = [];
    private volatile bool _kick;
    private DateTimeOffset _switchPortSeen;

    public DiscoveryService(IAdapterControl adapters, AdapterSessionManager sessions, SessionLog log)
    {
        _adapters = adapters;
        _sessions = sessions;
        _log = log;
    }

    public DiscoveryTable Table { get; } = new();
    public bool Running { get; private set; }
    public SwitchPortInfo? SwitchPort { get; private set; }
    /// <summary>What the finder can't do on this laptop, in plain English (for example Npcap missing).</summary>
    public IReadOnlyList<string> Limitations { get; private set; } = [];
    public bool NpcapAvailable { get; private set; }
    public bool Sweeping { get; private set; }
    public string? SweepStatus { get; private set; }

    /// <summary>Set by the server: true for addresses the DHCP server handed out, which aren't logged as finds.</summary>
    public Func<IPAddress, bool>? IsDhcpClient { get; set; }

    /// <summary>Raised when the device list, switch port or scan status changes. Worker thread.</summary>
    public event Action? Changed;

    public void Start(AdapterInfo adapter, IPAddress localIp)
    {
        if (Running) return;
        Table.Clear();
        SwitchPort = null;
        SweepStatus = null;
        lock (_gate) { _learnedTypes.Clear(); _pendingDescriptions.Clear(); _fetched.Clear(); }
        _adapterId = adapter.Id;
        _adapterName = adapter.Name;
        _interfaceIndex = adapter.Index;
        _local = adapter.Addresses;
        Table.SetOwnAddresses(adapter.Addresses.Select(a => a.Address));

        var limits = new List<string>();
        _sniffer = new RawIpSniffer(localIp, (buffer, length) => Handle(DiscoveryParser.ParseIp(DateTimeOffset.Now, buffer.AsSpan(0, length))));
        if (_sniffer.Start() is { } sniffProblem) { limits.Add(sniffProblem); _sniffer = null; }

        _mdns = TryOpen(localIp, DnsMessage.MdnsPort, [DnsMessage.MdnsGroup], 255, limits, "mDNS");
        _ssdp = TryOpen(localIp, Ssdp.Port, [Ssdp.Group], 2, limits, "UPnP");
        foreach (var port in VendorProtocols.ListenPorts)
            if (TryOpen(localIp, port, VendorProtocols.Groups(port), 2, limits, $"port {port}") is { } s) _vendorSockets.Add(s);

        NpcapAvailable = NpcapCapture.IsInstalled;
        if (NpcapAvailable)
        {
            _npcap = new NpcapCapture(adapter.Id, OnFrame);
            _npcap.Failed += problem => _log.Add(LogSeverity.Warning, LogCategory.Device, problem);
            if (_npcap.Start() is { } capProblem) { limits.Add(capProblem); _npcap = null; }
        }
        else
        {
            limits.Add("Npcap isn't installed, so the switch port (LLDP/CDP) can't be shown and quiet devices are only found by the subnet scan.");
        }

        Limitations = limits;
        foreach (var l in limits) _log.Add(LogSeverity.Info, LogCategory.Device, l);
        _log.Add(LogSeverity.Info, LogCategory.Device,
            $"Looking for devices on \"{adapter.Name}\" (broadcasts, mDNS, UPnP, {string.Join(", ", VendorProtocols.Names)}{(_npcap is not null ? ", ARP, LLDP and CDP via Npcap" : "")}).");

        _cts = new CancellationTokenSource();
        Running = true;
        _loop = Task.Run(() => Loop(_cts.Token));
        Changed?.Invoke();
    }

    private DiscoverySocket? TryOpen(IPAddress localIp, int port, IEnumerable<IPAddress> groups, int ttl, List<string> limits, string what)
    {
        try
        {
            var socket = DiscoverySocket.Open(localIp, port, groups, ttl, OnDatagram);
            if (!socket.OnRequestedPort && _sniffer is null)
                limits.Add($"Another program holds UDP {port}, so {what} announcements are only seen when they're replies to this laptop.");
            return socket;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            limits.Add($"Couldn't open UDP {port} for {what} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Asks the link again straight away (after the link comes back up).</summary>
    public void Kick() => _kick = true;

    /// <summary>The cable was unplugged: the switch port may be different next time.</summary>
    public void LinkDown()
    {
        if (SwitchPort is null) return;
        SwitchPort = null;
        Changed?.Invoke();
    }

    private void OnDatagram(IPEndPoint from, int port, byte[] buffer, int length)
    {
        // The sniffer already sees everything these sockets receive; only use them when it isn't running.
        if (_sniffer is not null) return;
        Handle(DiscoveryParser.ParseUdp(DateTimeOffset.Now, from.Address, from.Port, port, buffer.AsSpan(0, length)));
    }

    private void OnFrame(byte[] frame)
    {
        var (sighting, port) = DiscoveryParser.ParseFrame(DateTimeOffset.Now, frame);
        if (sighting is not null) Add(sighting);
        if (port is not null)
        {
            _switchPortSeen = DateTimeOffset.Now;
            var old = SwitchPort;
            SwitchPort = port;
            if (old is null || old.Describe() != port.Describe())
            {
                _log.Add(LogSeverity.Info, LogCategory.Adapter, $"{port.Describe()} (from {port.Protocol}).", port.DetailText());
                Changed?.Invoke();
            }
        }
    }

    private void Handle(ParsedPacket parsed)
    {
        foreach (var s in parsed.Sightings) Add(s);
        if (parsed.DescriptionToFetch is { } uri && parsed.Sightings.FirstOrDefault() is { } first)
            lock (_gate) if (!_fetched.Contains(uri.AbsoluteUri)) _pendingDescriptions[IPv4.ToUInt32(first.Ip)] = uri;
        if (parsed.MdnsTypes.Count > 0)
            lock (_gate)
                foreach (var t in parsed.MdnsTypes)
                    if (_learnedTypes.Count < MaxLearnedTypes) _learnedTypes.Add(t + ".local");
    }

    private void Add(Sighting s)
    {
        var update = Table.Add(s);
        if (update is null) return;
        var d = update.Device;
        bool reachable = d.IsReachable(_local);
        bool dhcp = IsDhcpClient?.Invoke(d.Ip) == true;
        if (update.Change == DiscoveryChange.New && !dhcp)
        {
            var where = reachable ? "" : $" It has a static IP on another subnet: press Reach it to talk to it.";
            _log.Add(reachable ? LogSeverity.Info : LogSeverity.Warning, LogCategory.Device,
                $"Found {DiscoveryParser.Describe(d)} by {d.FoundBy[0]}.{where}", s.Detail);
        }
        else if (update.Change == DiscoveryChange.IpChanged && !dhcp)
        {
            _log.Add(LogSeverity.Info, LogCategory.Device, $"{DiscoveryParser.Describe(d)} moved from {update.PreviousIp} to {d.Ip}.", s.Detail);
        }
        Changed?.Invoke();
    }

    private async Task Loop(CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        int early = 0;
        var nextRound = started;
        var nextHousekeeping = started;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            try
            {
                if (now >= nextRound || _kick)
                {
                    _kick = false;
                    SendQueries();
                    early++;
                    nextRound = early < EarlyRounds.Length ? started + EarlyRounds[early] : now + RoundInterval;
                }
                if (now >= nextHousekeeping)
                {
                    nextHousekeeping = now + HousekeepingInterval;
                    Housekeeping(now);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Add(LogSeverity.Warning, LogCategory.Device, $"Device discovery hit a problem: {ex.Message}", ex.ToString());
            }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void SendQueries()
    {
        List<string> names;
        lock (_gate) names = [.. MdnsCatalog.ServiceTypes, .. _learnedTypes.Where(t => !MdnsCatalog.ServiceTypes.Contains(t))];
        // Keep each query small: a few names per packet.
        foreach (var chunk in names.Chunk(8)) _mdns?.Send(DnsMessage.MdnsGroup, DnsMessage.MdnsPort, DnsMessage.BuildQuery(chunk));
        _ssdp?.Send(Ssdp.Group, Ssdp.Port, Ssdp.BuildSearch());
        foreach (var q in VendorProtocols.Queries(_local))
        {
            var socket = _vendorSockets.FirstOrDefault(s => s.RequestedPort == q.SourcePort && q.SourcePort != 0) ?? _ssdp ?? _mdns;
            socket?.Send(q.Destination, q.Port, q.Payload);
        }
    }

    private void Housekeeping(DateTimeOffset now)
    {
        if (_adapterId is null) return;
        if (_adapters.GetAdapter(_adapterId) is { } adapter)
        {
            _local = adapter.Addresses;
            Table.SetOwnAddresses(adapter.Addresses.Select(a => a.Address));
        }
        foreach (var entry in WindowsArp.Read(_interfaceIndex))
            Add(new Sighting(now, DiscoverySource.ArpTable, entry.Ip) { Mac = entry.Mac });

        if (SwitchPort is { } sp && now - _switchPortSeen > (sp.TimeToLive is { } ttl && ttl > TimeSpan.Zero ? ttl * 2 : TimeSpan.FromMinutes(4)))
        {
            SwitchPort = null;
            Changed?.Invoke();
        }

        // Fetch UPnP descriptions only from devices this adapter can reach, so the request can't go out Wi-Fi instead.
        List<(uint Ip, Uri Uri)> ready;
        lock (_gate)
        {
            ready = _pendingDescriptions.Where(p => SubnetPlanner.IsReachable(IPv4.FromUInt32(p.Key), _local)).Select(p => (p.Key, p.Value)).ToList();
            foreach (var (ip, uri) in ready) { _pendingDescriptions.Remove(ip); _fetched.Add(uri.AbsoluteUri); }
        }
        foreach (var (ip, uri) in ready) _ = FetchDescription(IPv4.FromUInt32(ip), uri);
    }

    private async Task FetchDescription(IPAddress ip, Uri uri)
    {
        try
        {
            var xml = await _http.GetStringAsync(uri, _cts?.Token ?? CancellationToken.None);
            if (UpnpDescription.TryParse(xml) is { } d) Add(DiscoveryParser.FromDescription(DateTimeOffset.Now, ip, d, uri));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // The device answered SSDP but not HTTP; it's still listed.
        }
    }

    /// <summary>Scans the likely subnets for devices with static IPs. Runs in the background; progress is in <see cref="SweepStatus"/>.</summary>
    public void StartSweep(IReadOnlyList<AdapterInfo> allAdapters)
    {
        if (!Running || Sweeping || _adapterId is null) return;
        var adapter = _adapters.GetAdapter(_adapterId);
        if (adapter is null) return;
        var (targets, skipped) = SubnetPlanner.SweepTargets(adapter.Addresses, Table.Snapshot(), allAdapters.Where(a => a.Id != _adapterId));
        Sweeping = true;
        SweepStatus = "Starting the scan…";
        Changed?.Invoke();
        var ct = _cts!.Token;
        _sweep = Task.Run(async () =>
        {
            try
            {
                _log.Add(LogSeverity.Info, LogCategory.Device,
                    $"Scanning {string.Join(", ", targets.Select(t => t.Short))} for devices with static IPs.",
                    string.Join(Environment.NewLine, targets.Select(t => $"{t.Short}: {t.Reason}").Concat(skipped.Select(s => $"{s.Subnet}: skipped, {s.Reason}"))));
                foreach (var s in skipped)
                    _log.Add(LogSeverity.Warning, LogCategory.Device, $"Not scanning {s.Subnet}: {s.Reason}. Turn that adapter off to scan it.");
                var avoid = Table.Snapshot().Select(d => IPv4.ToUInt32(d.Ip)).ToHashSet();
                var helper = new SubnetHelper(this, _arp);
                var summary = await helper.SweepAsync(targets, avoid, p => { SweepStatus = p; Changed?.Invoke(); }, Add, ct);
                SweepStatus = summary.Found == 1 ? "Scan finished: 1 device answered." : $"Scan finished: {summary.Found} devices answered.";
                _log.Add(LogSeverity.Success, LogCategory.Device, SweepStatus, string.Join(Environment.NewLine, summary.Lines));
            }
            catch (OperationCanceledException)
            {
                SweepStatus = "Scan stopped.";
            }
            catch (Exception ex)
            {
                SweepStatus = $"The scan stopped: {ex.Message}";
                _log.Add(LogSeverity.Error, LogCategory.Device, SweepStatus, ex.ToString());
            }
            finally
            {
                Sweeping = false;
                Changed?.Invoke();
            }
        }, CancellationToken.None);
    }

    /// <summary>Gives the laptop an address on the device's subnet until Stop. Throws with a plain-English reason.</summary>
    public async Task<string> ReachAsync(DiscoveredDevice device)
    {
        if (!Running) throw new AdapterOperationException("Start the server first.");
        var avoid = Table.Snapshot().Select(d => IPv4.ToUInt32(d.Ip)).ToHashSet();
        var result = await new SubnetHelper(this, _arp).ReachAsync(device.Ip, avoid, _cts!.Token);
        if (result.AlreadyReachable) return $"This laptop can already reach {device.Ip}.";
        if (result.Address is null) throw new AdapterOperationException($"Couldn't add an address on {device.Ip}'s subnet: {result.Problem}.");
        _local = CurrentAddresses();
        var message = $"Added {result.Address}/{result.PrefixLength} to \"{_adapterName}\" so this laptop can reach {device.Ip}. It's removed when you press Stop.";
        _log.Add(LogSeverity.Success, LogCategory.Adapter, message);
        Kick();
        Changed?.Invoke();
        return message;
    }

    // ---------- IHelperAddressHost: temporary addresses through the backup session ----------

    public IReadOnlyList<AddressAssignment> CurrentAddresses() =>
        _adapterId is not null && _adapters.GetAdapter(_adapterId) is { } a ? a.Addresses : [];

    void IHelperAddressHost.Add(IPAddress address, int prefixLength) => _sessions.AddTemporaryAddress(address, prefixLength);

    void IHelperAddressHost.Remove(IPAddress address)
    {
        try { _sessions.RemoveTemporaryAddress(address); }
        catch (Exception ex) when (ex is AdapterOperationException or InvalidOperationException)
        {
            _log.Add(LogSeverity.Warning, LogCategory.Adapter, $"Couldn't remove the temporary address {address} yet; Stop removes it.", ex.Message);
        }
    }

    public HelperState State(IPAddress address)
    {
        var a = _adapterId is null ? null : _adapters.GetAdapter(_adapterId);
        if (a is null) return HelperState.Pending;
        if (a.DuplicateAddresses.Any(d => d.Equals(address))) return HelperState.Duplicate;
        return a.HasAddress(address) ? HelperState.Ready : HelperState.Pending;
    }

    public async Task StopAsync()
    {
        if (!Running && _loop is null) return;
        _cts?.Cancel();
        if (_sweep is not null) { try { await _sweep; } catch { /* reported */ } }
        if (_loop is not null) { try { await _loop; } catch { /* stopped */ } }
        _npcap?.Dispose();
        if (_sniffer is not null) await _sniffer.DisposeAsync();
        if (_mdns is not null) await _mdns.DisposeAsync();
        if (_ssdp is not null) await _ssdp.DisposeAsync();
        foreach (var s in _vendorSockets) await s.DisposeAsync();
        _vendorSockets.Clear();
        _npcap = null; _sniffer = null; _mdns = null; _ssdp = null; _loop = null; _sweep = null;
        _cts?.Dispose();
        _cts = null;
        Running = false;
        Sweeping = false;
        SwitchPort = null;
        Changed?.Invoke();
    }

    /// <summary>For crash handlers: stop sending and scanning without waiting.</summary>
    public void Abort()
    {
        try { _cts?.Cancel(); } catch { }
        try { _npcap?.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
