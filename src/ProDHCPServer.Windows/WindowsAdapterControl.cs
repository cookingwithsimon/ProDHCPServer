using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

/// <summary>
/// Reads live adapter state from the .NET network APIs, reads the persisted IPv4 configuration
/// from the registry (works while the cable is unplugged), and changes settings with netsh.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdapterControl : IAdapterControl
{
    private const string TcpipInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string NetworkClass = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\";
    private static readonly string[] AlreadyDone = ["already enabled", "already configured", "already exists"];

    private const string AdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private readonly Dictionary<string, (long Speed, bool? Duplex)> _duplexCache = [];
    private readonly Dictionary<string, AdapterDriver?> _driverCache = [];

    public IReadOnlyList<AdapterInfo> ListAdapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var info = TryDescribe(ni);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    public AdapterInfo? GetAdapter(string adapterId)
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => SameId(n.Id, adapterId));
        return ni is null ? null : TryDescribe(ni);
    }

    private AdapterInfo? TryDescribe(NetworkInterface ni)
    {
        IPInterfaceProperties props;
        IPv4InterfaceProperties? v4;
        try
        {
            props = ni.GetIPProperties();
            v4 = props.GetIPv4Properties();
        }
        catch (NetworkInformationException)
        {
            return null; // IPv4 not bound to this adapter
        }
        if (v4 is null) return null;

        var up = ni.OperationalStatus == OperationalStatus.Up;
        var addresses = new List<AddressAssignment>();
        var duplicates = new List<IPAddress>();
        foreach (var ua in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var state = SafeDad(ua);
            if (state == DuplicateAddressDetectionState.Duplicate) duplicates.Add(ua.Address);
            else if (state is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated or null)
                addresses.Add(new AddressAssignment(ua.Address, ua.PrefixLength));
        }

        long speed = up ? Math.Max(0, ni.Speed) : 0;
        long? received = null, sent = null;
        try
        {
            var stats = ni.GetIPStatistics();
            received = stats.BytesReceived;
            sent = stats.BytesSent;
        }
        catch (NetworkInformationException) { /* counters not available for this adapter */ }
        return new AdapterInfo
        {
            Id = ni.Id,
            Index = v4.Index,
            Name = ni.Name,
            Description = ni.Description,
            Mac = ni.GetPhysicalAddress() is { } pa && pa.GetAddressBytes().Length == 6 ? MacAddress.FromBytes(pa.GetAddressBytes()) : MacAddress.Empty,
            Kind = AdapterRules.Classify(ni.NetworkInterfaceType.ToString(), ni.Name, ni.Description),
            IsEnabled = true, // disabled adapters are not listed by Windows at all
            MediaConnected = up,
            SpeedBitsPerSecond = speed,
            FullDuplex = up ? Duplex(ni.Id, speed) : null,
            DhcpEnabled = v4.IsDhcpEnabled,
            Addresses = addresses,
            DuplicateAddresses = duplicates,
            Gateways = props.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPv4.IsZero(a)).ToList(),
            DnsServers = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
            DhcpServer = SafeDhcpServers(props).FirstOrDefault(),
            BytesReceived = received,
            BytesSent = sent,
            Driver = Driver(ni.Id),
        };
    }

    /// <summary>Driver provider, version, date and hardware ID from the network class key. Cached per adapter.</summary>
    private AdapterDriver? Driver(string id)
    {
        lock (_driverCache)
        {
            if (_driverCache.TryGetValue(id, out var cached)) return cached;
        }
        AdapterDriver? driver = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(AdapterClass);
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                if (!name.All(char.IsAsciiDigit)) continue;
                using var key = cls!.OpenSubKey(name);
                if (key?.GetValue("NetCfgInstanceId") is not string instance || !SameId(instance, id)) continue;
                DateOnly? date = key.GetValue("DriverDate") is string text
                    && DateOnly.TryParseExact(text, ["M-d-yyyy", "MM-dd-yyyy"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed : null;
                driver = new AdapterDriver(key.GetValue("ProviderName") as string, key.GetValue("DriverVersion") as string, date,
                    key.GetValue("MatchingDeviceId") as string);
                break;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            driver = null;
        }
        lock (_driverCache) _driverCache[id] = driver;
        return driver;
    }

    private static DuplicateAddressDetectionState? SafeDad(UnicastIPAddressInformation ua)
    {
        try { return ua.DuplicateAddressDetectionState; } catch (PlatformNotSupportedException) { return null; }
    }

    private static IEnumerable<IPAddress> SafeDhcpServers(IPInterfaceProperties props)
    {
        try { return props.DhcpServerAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPv4.IsZero(a)).ToList(); }
        catch (PlatformNotSupportedException) { return []; }
    }

    /// <summary>Duplex isn't in the .NET API; ask WMI once per link speed change.</summary>
    private bool? Duplex(string id, long speed)
    {
        lock (_duplexCache)
        {
            if (_duplexCache.TryGetValue(id, out var cached) && cached.Speed == speed) return cached.Duplex;
        }
        bool? duplex = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                $"SELECT FullDuplex FROM MSFT_NetAdapter WHERE DeviceID = '{id.Replace("'", "")}'");
            foreach (ManagementObject mo in searcher.Get())
                using (mo) duplex = mo["FullDuplex"] as bool?;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            duplex = null;
        }
        lock (_duplexCache) _duplexCache[id] = (speed, duplex);
        return duplex;
    }

    public AdapterSnapshot Capture(string adapterId)
    {
        var live = GetAdapter(adapterId) ?? throw new AdapterOperationException("That adapter is no longer present (USB adapter unplugged?).");
        using var key = Registry.LocalMachine.OpenSubKey(TcpipInterfaces + NormaliseGuid(adapterId))
            ?? throw new AdapterOperationException($"Couldn't read the saved IP settings for \"{live.Name}\".", $"Missing registry key HKLM\\{TcpipInterfaces}{NormaliseGuid(adapterId)}");

        bool dhcp = Convert.ToInt32(key.GetValue("EnableDHCP", 1)) != 0;
        var ips = ReadMulti(key, "IPAddress");
        var masks = ReadMulti(key, "SubnetMask");
        var statics = new List<AddressAssignment>();
        if (!dhcp)
        {
            for (int i = 0; i < ips.Length; i++)
            {
                if (!IPv4.TryParse(ips[i], out var ip) || IPv4.IsZero(ip)) continue;
                int prefix = i < masks.Length && IPv4.TryParse(masks[i], out var m) ? IPv4.MaskToPrefix(m) : 24;
                statics.Add(new AddressAssignment(ip, prefix < 0 ? 24 : prefix));
            }
        }
        var gateways = dhcp ? [] : ReadMulti(key, "DefaultGateway").Select(g => IPv4.TryParse(g, out var a) ? a : null).OfType<IPAddress>().ToList();
        var nameServer = (key.GetValue("NameServer") as string ?? "").Trim();
        var dns = nameServer.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => IPv4.TryParse(d, out var a) ? a : null).OfType<IPAddress>().ToList();

        return new AdapterSnapshot
        {
            AdapterId = live.Id,
            InterfaceIndex = live.Index,
            AdapterName = live.Name,
            DhcpEnabled = dhcp,
            StaticAddresses = statics,
            Gateways = gateways,
            DnsFromDhcp = dns.Count == 0,
            StaticDnsServers = dns,
            CapturedAt = DateTimeOffset.Now,
        };
    }

    public void ApplyStaticAddress(string adapterId, IPAddress address, int prefixLength)
    {
        var idx = IndexOf(adapterId);
        CommandRunner.Netsh($"set {address}/{prefixLength}",
            "interface", "ipv4", "set", "address", $"name={idx}", "source=static", $"address={address}", $"mask={IPv4.PrefixToMaskAddress(prefixLength)}", "gateway=none");
        CommandRunner.Netsh("clear the DNS servers", AlreadyDone,
            "interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=static", "address=none", "register=none", "validate=no");
    }

    public void AddAddress(string adapterId, IPAddress address, int prefixLength) =>
        CommandRunner.Netsh($"add {address}/{prefixLength}", AlreadyDone,
            "interface", "ipv4", "add", "address", $"name={IndexOf(adapterId)}", $"address={address}", $"mask={IPv4.PrefixToMaskAddress(prefixLength)}");

    public void RemoveAddress(string adapterId, IPAddress address) =>
        CommandRunner.Netsh($"remove {address}", ["not found"],
            "interface", "ipv4", "delete", "address", $"name={IndexOf(adapterId)}", $"address={address}");

    public void Restore(AdapterSnapshot s)
    {
        var idx = IndexOf(s.AdapterId);
        if (s.DhcpEnabled || s.StaticAddresses.Count == 0)
        {
            CommandRunner.Netsh("switch the adapter back to automatic (DHCP)", AlreadyDone,
                "interface", "ipv4", "set", "address", $"name={idx}", "source=dhcp");
        }
        else
        {
            var first = s.StaticAddresses[0];
            var args = new List<string> { "interface", "ipv4", "set", "address", $"name={idx}", "source=static", $"address={first.Address}", $"mask={IPv4.PrefixToMaskAddress(first.PrefixLength)}" };
            if (s.Gateways.Count > 0) { args.Add($"gateway={s.Gateways[0]}"); args.Add("gwmetric=0"); }
            else args.Add("gateway=none");
            CommandRunner.Netsh($"put back {first}", [.. args]);
            foreach (var extra in s.StaticAddresses.Skip(1))
                AddAddress(s.AdapterId, extra.Address, extra.PrefixLength);
        }

        if (s.DnsFromDhcp)
        {
            CommandRunner.Netsh("switch DNS back to automatic", AlreadyDone,
                "interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=dhcp");
        }
        else
        {
            CommandRunner.Netsh($"put back DNS server {s.StaticDnsServers[0]}",
                "interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=static", $"address={s.StaticDnsServers[0]}", "register=primary", "validate=no");
            for (int i = 1; i < s.StaticDnsServers.Count; i++)
                CommandRunner.Netsh($"put back DNS server {s.StaticDnsServers[i]}", AlreadyDone,
                    "interface", "ipv4", "add", "dnsservers", $"name={idx}", $"address={s.StaticDnsServers[i]}", $"index={i + 1}", "validate=no");
        }

        // Clear the adapter's ARP cache, which drops any permanent entries the server added for
        // clients and left behind after a crash. Windows rebuilds the cache on demand; failure is harmless.
        CommandRunner.Run("netsh.exe", ["interface", "ipv4", "delete", "neighbors", $"interface={idx}"]);
    }

    public void SetAdapterEnabled(string adapterId, bool enabled)
    {
        var name = ConnectionName(adapterId) ?? throw new AdapterOperationException("Couldn't find that adapter's name to turn it on or off.");
        CommandRunner.Netsh($"turn {(enabled ? "on" : "off")} \"{name}\"", ["already"],
            "interface", "set", "interface", $"name={name}", $"admin={(enabled ? "enabled" : "disabled")}");
    }

    private int IndexOf(string adapterId)
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => SameId(n.Id, adapterId))
            ?? throw new AdapterOperationException("That adapter is no longer present (USB adapter unplugged?).");
        return ni.GetIPProperties().GetIPv4Properties()?.Index
            ?? throw new AdapterOperationException($"IPv4 is turned off on \"{ni.Name}\". Enable \"Internet Protocol Version 4\" in its properties.");
    }

    /// <summary>The adapter's name in Network Connections; read from the registry so it works while disabled.</summary>
    private static string? ConnectionName(string adapterId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(NetworkClass + NormaliseGuid(adapterId) + @"\Connection");
        return key?.GetValue("Name") as string;
    }

    private static string[] ReadMulti(RegistryKey key, string name) => key.GetValue(name) switch
    {
        string[] multi => multi,
        string single when single.Length > 0 => [single],
        _ => [],
    };

    private static string NormaliseGuid(string id) => id.StartsWith('{') ? id : "{" + id + "}";

    private static bool SameId(string a, string b) =>
        string.Equals(NormaliseGuid(a), NormaliseGuid(b), StringComparison.OrdinalIgnoreCase);
}
