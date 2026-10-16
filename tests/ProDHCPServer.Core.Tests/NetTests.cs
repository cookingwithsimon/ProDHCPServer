using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Tests;

public class NetTests
{
    [Theory]
    [InlineData("00:1d:c1:11:22:33")]
    [InlineData("00-1D-C1-11-22-33")]
    [InlineData("001DC1112233")]
    public void Mac_parses_common_formats(string text)
    {
        Assert.Equal("00:1D:C1:11:22:33", MacAddress.Parse(text).ToString());
    }

    [Theory]
    [InlineData("00:1D:C1:11:22")]
    [InlineData("zz:1D:C1:11:22:33")]
    [InlineData("")]
    public void Mac_rejects_garbage(string text) => Assert.False(MacAddress.TryParse(text, out _));

    [Fact]
    public void Mac_flags()
    {
        Assert.True(MacAddress.Parse("02:00:00:00:00:01").IsLocallyAdministered);
        Assert.False(MacAddress.Parse("00:1D:C1:00:00:01").IsLocallyAdministered);
        Assert.True(MacAddress.Parse("FF:FF:FF:FF:FF:FF").IsBroadcast);
        Assert.Equal(0x001DC1u, MacAddress.Parse("00:1D:C1:00:00:01").Oui);
    }

    [Fact]
    public void Oui_lookup_finds_audinate_and_flags_random_macs()
    {
        var db = OuiDatabase.Default;
        Assert.True(db.Count > 30000);
        Assert.StartsWith("Audinate", db.Lookup(MacAddress.Parse("00:1D:C1:12:34:56")));
        Assert.Equal("Private / randomised MAC", db.Lookup(MacAddress.Parse("06:11:22:33:44:55")));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.001.1", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData(" 10.0.0.1 ", true)]
    public void IPv4_parse_is_strict(string text, bool ok) => Assert.Equal(ok, IPv4.TryParse(text, out _));

    [Fact]
    public void IPv4_subnet_math()
    {
        var ip = IPv4.Parse("192.168.100.37");
        Assert.Equal("192.168.100.0", IPv4.NetworkAddress(ip, 24).ToString());
        Assert.Equal("192.168.100.255", IPv4.BroadcastAddress(ip, 24).ToString());
        Assert.Equal("255.255.252.0", IPv4.PrefixToMaskAddress(22).ToString());
        Assert.Equal(22, IPv4.MaskToPrefix(IPv4.Parse("255.255.252.0")));
        Assert.Equal(-1, IPv4.MaskToPrefix(IPv4.Parse("255.0.255.0")));
        Assert.True(IPv4.IsApipa(IPv4.Parse("169.254.3.4")));
        Assert.True(IPv4.SubnetsOverlap(IPv4.Parse("192.168.0.5"), 16, IPv4.Parse("192.168.100.1"), 24));
        Assert.False(IPv4.SubnetsOverlap(IPv4.Parse("192.168.1.5"), 24, IPv4.Parse("192.168.100.1"), 24));
    }

    [Theory]
    [InlineData("Ethernet", "Ethernet", "Intel(R) Ethernet Connection I219-LM", AdapterKind.Ethernet)]
    [InlineData("Ethernet", "Ethernet 3", "Realtek USB GbE Family Controller", AdapterKind.Ethernet)]
    [InlineData("Wireless80211", "Wi-Fi", "Intel(R) Wi-Fi 6E AX211", AdapterKind.WiFi)]
    [InlineData("Ethernet", "Ethernet 2", "TAP-Windows Adapter V9", AdapterKind.Vpn)]
    [InlineData("Ethernet", "Local Area Connection", "PANGP Virtual Ethernet Adapter Secure", AdapterKind.Vpn)]
    [InlineData("Ethernet", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", AdapterKind.Virtual)]
    [InlineData("Ethernet", "Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", AdapterKind.Virtual)]
    [InlineData("Ppp", "Office VPN", "WAN Miniport (IKEv2)", AdapterKind.Vpn)]
    public void Adapter_classification(string type, string name, string description, AdapterKind expected) =>
        Assert.Equal(expected, AdapterRules.Classify(type, name, description));

    [Fact]
    public void Overlap_detection_ignores_serving_adapter_apipa_and_disconnected()
    {
        AdapterInfo A(string id, string ip, int prefix, bool up = true) => new()
        {
            Id = id, Index = 1, Name = id, MediaConnected = up, Addresses = [new(IPv4.Parse(ip), prefix)],
        };
        var all = new[]
        {
            A("eth", "192.168.100.1", 24),
            A("wifi", "192.168.100.40", 24),
            A("other", "192.168.0.10", 16),
            A("apipa", "169.254.1.1", 16),
            A("down", "192.168.100.9", 24, up: false),
            A("far", "10.0.0.5", 8),
        };
        var hits = AdapterRules.OverlappingAdapters(all, "eth", IPv4.Parse("192.168.100.1"), 24).Select(a => a.Id);
        Assert.Equal(["wifi", "other"], hits);
    }

    [Fact]
    public void Settings_to_config_reports_every_bad_field()
    {
        var (config, errors) = new Profiles.Profile { Name = "t", ServerIp = "192.168.100.300", DnsServers = "8.8.8.8, nope" }.ToConfig();
        Assert.Null(config);
        Assert.Equal(2, errors.Count);

        var (ok, none) = new Profiles.Profile { Name = "t", Router = "192.168.100.254", DnsServers = "1.1.1.1 8.8.8.8" }.ToConfig();
        Assert.Empty(none);
        Assert.Equal(2, ok!.DnsServers.Count);
    }

    [Fact]
    public void Settings_store_survives_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prodhcp-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "garbage");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal("192.168.100.1", store.Load().ServerIp);
            store.Save(new AppSettings { LastAdapterId = "abc" });
            Assert.Equal("abc", store.Load().LastAdapterId);
        }
        finally { File.Delete(path); }
    }
}
