using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Adapters;

/// <summary>Pure helpers for classifying adapters and spotting routing conflicts.</summary>
public static class AdapterRules
{
    private static readonly string[] VpnHints =
    [
        "vpn", "tap-windows", "tap adapter", "wireguard", "wintun", "openvpn", "anyconnect", "globalprotect",
        "pangp", "fortinet", "forticlient", "juniper", "pulse secure", "zscaler", "nordlynx", "tailscale", "zerotier",
        "sonicwall", "check point", "ppp adapter", "wan miniport",
    ];

    private static readonly string[] VirtualHints =
    [
        "hyper-v", "vethernet", "virtualbox", "vmware", "loopback", "bluetooth", "wi-fi direct", "microsoft wi-fi direct",
        "npcap", "docker", "wsl", "kernel debug", "teredo", "isatap", "6to4",
    ];

    /// <summary>
    /// Plain-English warning for adapter/driver combinations known to stop receiving while the link
    /// stays up, or null. Currently: Realtek RTL8152/8153 USB adapters on drivers older than 2019
    /// (including Windows' own 2015 inbox driver).
    /// </summary>
    public static string? KnownDriverProblem(AdapterInfo adapter)
    {
        var d = adapter.Driver;
        var hw = d?.HardwareId?.ToUpperInvariant() ?? "";
        bool realtekUsb = hw.Contains("VID_0BDA&PID_8153") || hw.Contains("VID_0BDA&PID_8152")
            || adapter.Description.Contains("Realtek USB GbE", StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains("Realtek USB FE", StringComparison.OrdinalIgnoreCase);
        if (!realtekUsb || d?.Date is not { } date || date >= new DateOnly(2019, 1, 1)) return null;
        return $"This Realtek USB adapter is on an old driver ({d}). On old drivers these adapters are known to stop receiving after a few seconds while still showing connected.";
    }

    /// <summary>
    /// Classifies from Windows' interface type plus name/description keywords.
    /// <paramref name="interfaceType"/> is the NetworkInterfaceType name (Ethernet, Wireless80211, Ppp, Tunnel...).
    /// </summary>
    public static AdapterKind Classify(string interfaceType, string name, string description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (interfaceType is "Ppp" or "Tunnel" || VpnHints.Any(text.Contains)) return AdapterKind.Vpn;
        if (VirtualHints.Any(text.Contains)) return AdapterKind.Virtual;
        if (interfaceType == "Wireless80211" || text.Contains("wi-fi") || text.Contains("wireless") || text.Contains("wlan") || text.Contains("802.11"))
            return AdapterKind.WiFi;
        if (interfaceType is "Ethernet" or "GigabitEthernet" or "FastEthernetT" or "FastEthernetFx" or "Ethernet3Megabit")
            return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }

    /// <summary>Other connected adapters with an address in a subnet that overlaps the serving subnet.</summary>
    public static IReadOnlyList<AdapterInfo> OverlappingAdapters(IEnumerable<AdapterInfo> all, string servingAdapterId, System.Net.IPAddress serverIp, int prefixLength) =>
        all.Where(a => a.Id != servingAdapterId && a.IsEnabled && a.MediaConnected)
           .Where(a => a.Addresses.Any(x => !IPv4.IsApipa(x.Address) && IPv4.SubnetsOverlap(x.Address, x.PrefixLength, serverIp, prefixLength)))
           .ToList();

    public static IReadOnlyList<AdapterInfo> ConnectedVpns(IEnumerable<AdapterInfo> all) =>
        all.Where(a => a.Kind == AdapterKind.Vpn && a.IsEnabled && a.MediaConnected && a.Addresses.Count > 0).ToList();

    /// <summary>Sort order for the picker: physical Ethernet with link first, then the rest.</summary>
    public static IEnumerable<AdapterInfo> PickerOrder(IEnumerable<AdapterInfo> all) =>
        all.OrderBy(a => a.Kind switch { AdapterKind.Ethernet => 0, AdapterKind.Other => 1, AdapterKind.WiFi => 2, AdapterKind.Virtual => 3, _ => 4 })
           .ThenByDescending(a => a.MediaConnected)
           .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
}
