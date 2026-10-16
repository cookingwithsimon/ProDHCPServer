using CommunityToolkit.Mvvm.ComponentModel;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.App.ViewModels;

/// <summary>The server settings form. Phase 2 turns this into saved profiles.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings s) => Load(s);

    public static IReadOnlyList<PrefixOption> Prefixes { get; } =
        Enumerable.Range(8, 23).Reverse().Select(p => new PrefixOption(p)).ToList();

    public static IReadOnlyList<string> Themes { get; } = ["System", "Dark", "Light"];

    [ObservableProperty] private string _serverIp = "";
    [ObservableProperty] private PrefixOption _prefix = new(24);
    [ObservableProperty] private string _poolStart = "";
    [ObservableProperty] private string _poolEnd = "";
    [ObservableProperty] private string _leaseMinutes = "60";
    [ObservableProperty] private string _router = "";
    [ObservableProperty] private string _dnsServers = "";
    [ObservableProperty] private bool _alwaysBroadcast;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private string _errors = "";

    // Any edit clears the error text straight away; it is checked again when the field loses focus or on Save.
    partial void OnServerIpChanged(string value) => Errors = "";
    partial void OnPrefixChanged(PrefixOption value) => Errors = "";
    partial void OnPoolStartChanged(string value) => Errors = "";
    partial void OnPoolEndChanged(string value) => Errors = "";
    partial void OnLeaseMinutesChanged(string value) => Errors = "";
    partial void OnRouterChanged(string value) => Errors = "";
    partial void OnDnsServersChanged(string value) => Errors = "";

    public void Load(AppSettings s)
    {
        ServerIp = s.ServerIp;
        Prefix = Prefixes.FirstOrDefault(p => p.Length == s.PrefixLength) ?? new PrefixOption(24);
        PoolStart = s.PoolStart;
        PoolEnd = s.PoolEnd;
        LeaseMinutes = s.LeaseMinutes.ToString();
        Router = s.Router ?? "";
        DnsServers = s.DnsServers ?? "";
        AlwaysBroadcast = s.AlwaysBroadcast;
        Theme = s.Theme;
    }

    public AppSettings ApplyTo(AppSettings s) => s with
    {
        ServerIp = ServerIp.Trim(),
        PrefixLength = Prefix.Length,
        PoolStart = PoolStart.Trim(),
        PoolEnd = PoolEnd.Trim(),
        LeaseMinutes = int.TryParse(LeaseMinutes, out var m) ? m : -1,
        Router = string.IsNullOrWhiteSpace(Router) ? null : Router.Trim(),
        DnsServers = string.IsNullOrWhiteSpace(DnsServers) ? null : DnsServers.Trim(),
        AlwaysBroadcast = AlwaysBroadcast,
        Theme = Theme,
    };

    /// <summary>Validates; on success returns the config and clears the error text.</summary>
    public DhcpServerConfig? TryBuild(AppSettings current)
    {
        if (!int.TryParse(LeaseMinutes, out var minutes) || minutes <= 0)
        {
            Errors = "Lease time must be a whole number of minutes.";
            return null;
        }
        var (config, errors) = ApplyTo(current).ToConfig();
        Errors = string.Join(Environment.NewLine, errors);
        return config;
    }

    /// <summary>Fills the pool as .100–.199 (or the nearest fit) of the server's subnet.</summary>
    public void SuggestPool()
    {
        if (!IPv4.TryParse(ServerIp, out var server)) return;
        uint net = IPv4.ToUInt32(IPv4.NetworkAddress(server, Prefix.Length));
        uint size = (uint)(1L << (32 - Prefix.Length));
        uint start = size >= 256 ? 100u : Math.Max(2u, size / 4);
        uint end = size >= 256 ? 199u : size - 2;
        PoolStart = IPv4.FromUInt32(net + start).ToString();
        PoolEnd = IPv4.FromUInt32(net + end).ToString();
    }
}

public sealed record PrefixOption(int Length)
{
    public override string ToString() => $"/{Length}  ({IPv4.PrefixToMaskAddress(Length)})";
}
