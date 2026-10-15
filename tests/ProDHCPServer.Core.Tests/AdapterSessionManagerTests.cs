using System.Net;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Tests;

/// <summary>An in-memory adapter whose "persisted config" behaves like Windows' registry settings.</summary>
internal sealed class FakeAdapterControl : IAdapterControl
{
    public sealed class State
    {
        public required string Id;
        public required string Name;
        public bool Present = true;
        public bool Enabled = true;
        public bool Dhcp = true;
        public List<AddressAssignment> Static = [];
        public List<IPAddress> Gateways = [];
        public bool DnsFromDhcp = true;
        public List<IPAddress> Dns = [];
    }

    public Dictionary<string, State> Adapters { get; } = [];
    public List<string> Calls { get; } = [];
    public Action<string>? BeforeCall { get; set; }
    public Exception? FailApply { get; set; }
    public Exception? FailRestore { get; set; }
    /// <summary>Simulates Windows silently ignoring part of a restore.</summary>
    public bool RestoreDropsGateway { get; set; }

    public State Add(string id, string name, Action<State>? setup = null)
    {
        var s = new State { Id = id, Name = name };
        setup?.Invoke(s);
        Adapters[id] = s;
        return s;
    }

    public IReadOnlyList<AdapterInfo> ListAdapters() => Adapters.Values.Where(a => a.Present).Select(ToInfo).ToList();

    public AdapterInfo? GetAdapter(string adapterId) =>
        Adapters.TryGetValue(adapterId, out var s) && s.Present ? ToInfo(s) : null;

    private static AdapterInfo ToInfo(State s) => new()
    {
        Id = s.Id, Index = 1, Name = s.Name, IsEnabled = s.Enabled, MediaConnected = true, DhcpEnabled = s.Dhcp,
        Addresses = s.Static, Kind = AdapterKind.Ethernet,
    };

    public AdapterSnapshot Capture(string adapterId)
    {
        Calls.Add($"capture {adapterId}");
        var s = Adapters[adapterId];
        return new AdapterSnapshot
        {
            AdapterId = s.Id, InterfaceIndex = 1, AdapterName = s.Name, DhcpEnabled = s.Dhcp,
            StaticAddresses = s.Dhcp ? [] : s.Static.ToList(), Gateways = s.Dhcp ? [] : s.Gateways.ToList(),
            DnsFromDhcp = s.DnsFromDhcp, StaticDnsServers = s.Dns.ToList(),
        };
    }

    public void ApplyStaticAddress(string adapterId, IPAddress address, int prefixLength)
    {
        BeforeCall?.Invoke("apply");
        Calls.Add($"apply {address}/{prefixLength}");
        var s = Adapters[adapterId];
        s.Dhcp = false;
        s.Static = [new AddressAssignment(address, prefixLength)];
        s.Gateways.Clear();
        s.DnsFromDhcp = false;
        s.Dns.Clear();
        if (FailApply is not null) throw FailApply;
    }

    public void AddAddress(string adapterId, IPAddress address, int prefixLength)
    {
        Calls.Add($"add {address}");
        Adapters[adapterId].Static.Add(new AddressAssignment(address, prefixLength));
    }

    public void RemoveAddress(string adapterId, IPAddress address)
    {
        Calls.Add($"remove {address}");
        Adapters[adapterId].Static.RemoveAll(a => a.Address.Equals(address));
    }

    public void Restore(AdapterSnapshot snapshot)
    {
        Calls.Add("restore");
        if (FailRestore is not null) throw FailRestore;
        var s = Adapters[snapshot.AdapterId];
        s.Dhcp = snapshot.DhcpEnabled;
        s.Static = snapshot.StaticAddresses.ToList();
        s.Gateways = RestoreDropsGateway ? [] : snapshot.Gateways.ToList();
        s.DnsFromDhcp = snapshot.DnsFromDhcp;
        s.Dns = snapshot.StaticDnsServers.ToList();
    }

    public void SetAdapterEnabled(string adapterId, bool enabled)
    {
        Calls.Add($"{(enabled ? "enable" : "disable")} {adapterId}");
        Adapters[adapterId].Enabled = enabled;
    }
}

internal sealed class MemorySessionStore : ISessionStore
{
    public SessionRecord? Record { get; set; }
    public int Saves { get; private set; }
    public SessionRecord? Load() => Record;
    public void Save(SessionRecord record) { Record = record; Saves++; }
    public void Clear() => Record = null;
}

public class AdapterSessionManagerTests
{
    private static readonly IPAddress ServerIp = IPv4.Parse("192.168.100.1");
    private readonly FakeAdapterControl _adapters = new();
    private readonly MemorySessionStore _store = new();
    private readonly AdapterSessionManager _manager;

    public AdapterSessionManagerTests() => _manager = new AdapterSessionManager(_adapters, _store);

    private FakeAdapterControl.State StaticEthernet() => _adapters.Add("eth", "Ethernet", s =>
    {
        s.Dhcp = false;
        s.Static = [new(IPv4.Parse("10.0.0.50"), 24), new(IPv4.Parse("10.0.0.51"), 24)];
        s.Gateways = [IPv4.Parse("10.0.0.1")];
        s.DnsFromDhcp = false;
        s.Dns = [IPv4.Parse("10.0.0.2"), IPv4.Parse("1.1.1.1")];
    });

    [Fact]
    public void Backup_is_saved_before_the_adapter_is_changed()
    {
        _adapters.Add("eth", "Ethernet");
        SessionRecord? savedAtApply = null;
        _adapters.BeforeCall = call => { if (call == "apply") savedAtApply = _store.Record; };

        _manager.Begin("eth", ServerIp, 24);

        Assert.NotNull(savedAtApply);
        Assert.True(savedAtApply!.Snapshot.DhcpEnabled);
        Assert.Equal(["capture eth", "apply 192.168.100.1/24"], _adapters.Calls);
    }

    [Fact]
    public void Stop_puts_dhcp_adapter_back_to_automatic()
    {
        var eth = _adapters.Add("eth", "Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        Assert.False(eth.Dhcp);

        var result = _manager.End();

        Assert.True(result.Success, string.Join(" ", result.Problems));
        Assert.True(eth.Dhcp);
        Assert.True(eth.DnsFromDhcp);
        Assert.Null(_store.Record);
        Assert.Null(_manager.Active);
        Assert.Contains("Automatic (DHCP)", result.Summary);
    }

    [Fact]
    public void Stop_restores_static_addresses_gateway_and_dns_exactly()
    {
        var eth = StaticEthernet();
        _manager.Begin("eth", ServerIp, 24);
        Assert.Equal([new AddressAssignment(ServerIp, 24)], eth.Static);

        var result = _manager.End();

        Assert.True(result.Success);
        Assert.Equal(2, eth.Static.Count);
        Assert.Equal([IPv4.Parse("10.0.0.1")], eth.Gateways);
        Assert.Equal([IPv4.Parse("10.0.0.2"), IPv4.Parse("1.1.1.1")], eth.Dns);
    }

    [Fact]
    public void Failed_apply_rolls_back_and_explains()
    {
        var eth = StaticEthernet();
        _adapters.FailApply = new AdapterOperationException("The adapter refused the address.", "netsh exit code 1");

        var ex = Assert.Throws<AdapterOperationException>(() => _manager.Begin("eth", ServerIp, 24));

        Assert.Contains("refused the address", ex.Message);
        Assert.Contains("original settings were put back", ex.Message);
        Assert.Equal("netsh exit code 1", ex.Detail);
        Assert.Equal(2, eth.Static.Count);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void Failed_restore_keeps_backup_for_retry()
    {
        _adapters.Add("eth", "Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        _adapters.FailRestore = new AdapterOperationException("Access denied.");

        var result = _manager.End();

        Assert.False(result.Success);
        Assert.Contains(result.Problems, p => p.Contains("Access denied"));
        Assert.NotNull(_store.Record);

        _adapters.FailRestore = null;
        Assert.True(_manager.End().Success);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void Restore_is_verified_by_reading_the_adapter_back()
    {
        StaticEthernet();
        _manager.Begin("eth", ServerIp, 24);
        _adapters.RestoreDropsGateway = true;

        var result = _manager.End();

        Assert.False(result.Success);
        Assert.Contains(result.Problems, p => p.Contains("Gateway"));
        Assert.NotNull(_store.Record);
    }

    [Fact]
    public void Leftover_backup_from_a_crash_is_offered_and_blocks_a_new_start()
    {
        var eth = StaticEthernet();
        _manager.Begin("eth", ServerIp, 24);

        // Simulate the process dying: a fresh manager over the same store.
        var afterCrash = new AdapterSessionManager(_adapters, _store);
        var pending = afterCrash.FindPendingRecovery();
        Assert.NotNull(pending);
        Assert.Equal("Ethernet", pending!.Snapshot.AdapterName);

        var ex = Assert.Throws<AdapterOperationException>(() => afterCrash.Begin("eth", ServerIp, 24));
        Assert.Contains("unrestored backup", ex.Message);
        Assert.False(pending.Snapshot.DhcpEnabled); // the original backup was not overwritten

        Assert.True(afterCrash.Recover(pending).Success);
        Assert.Equal(2, eth.Static.Count);
        Assert.Null(afterCrash.FindPendingRecovery());
    }

    [Fact]
    public void Discarding_a_leftover_backup_clears_it()
    {
        _adapters.Add("eth", "Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        var afterCrash = new AdapterSessionManager(_adapters, _store);
        afterCrash.DiscardPendingRecovery();
        Assert.Null(afterCrash.FindPendingRecovery());
    }

    [Fact]
    public void Missing_adapter_keeps_backup_and_says_to_plug_it_in()
    {
        var eth = _adapters.Add("eth", "USB Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        eth.Present = false;

        var result = _manager.End();

        Assert.False(result.Success);
        Assert.Contains(result.Problems, p => p.Contains("unplugged"));
        Assert.NotNull(_store.Record);
    }

    [Fact]
    public void Disabled_wifi_is_recorded_first_and_turned_back_on()
    {
        _adapters.Add("eth", "Ethernet");
        var wifi = _adapters.Add("wifi", "Wi-Fi");
        _manager.Begin("eth", ServerIp, 24);

        _manager.DisableForSession(_adapters.GetAdapter("wifi")!);
        Assert.False(wifi.Enabled);
        Assert.Single(_store.Record!.DisabledAdapters);

        Assert.True(_manager.End().Success);
        Assert.True(wifi.Enabled);
    }

    [Fact]
    public void Temporary_addresses_are_removed_on_restore()
    {
        var eth = _adapters.Add("eth", "Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        _manager.AddTemporaryAddress(IPv4.Parse("192.168.1.250"), 24);
        Assert.Single(_store.Record!.TemporaryAddresses);

        Assert.True(_manager.End().Success);
        Assert.Contains("remove 192.168.1.250", _adapters.Calls);
        Assert.True(eth.Dhcp);
    }

    [Fact]
    public void Cannot_start_twice()
    {
        _adapters.Add("eth", "Ethernet");
        _manager.Begin("eth", ServerIp, 24);
        Assert.Throws<InvalidOperationException>(() => _manager.Begin("eth", ServerIp, 24));
    }

    [Fact]
    public void End_with_nothing_running_is_a_no_op()
    {
        Assert.True(_manager.End().Success);
    }

    [Fact]
    public void File_store_round_trips_and_writes_atomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prodhcp-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(Path.Combine(dir, "session.json"));
            Assert.Null(store.Load());
            var record = new SessionRecord
            {
                Snapshot = new AdapterSnapshot
                {
                    AdapterId = "{GUID}", InterfaceIndex = 12, AdapterName = "Ethernet 2", DhcpEnabled = false,
                    StaticAddresses = [new(IPv4.Parse("10.0.0.50"), 24)], Gateways = [IPv4.Parse("10.0.0.1")],
                    DnsFromDhcp = false, StaticDnsServers = [IPv4.Parse("10.0.0.2")],
                },
                ServerIp = ServerIp,
                PrefixLength = 24,
                DisabledAdapters = [new DisabledAdapter("{WIFI}", "Wi-Fi")],
            };
            store.Save(record);
            Assert.False(File.Exists(store.Path + ".tmp"));

            var loaded = store.Load()!;
            Assert.Equal(record.Snapshot.StaticAddresses, loaded.Snapshot.StaticAddresses);
            Assert.Equal(record.Snapshot.Gateways, loaded.Snapshot.Gateways);
            Assert.Equal(record.Snapshot.StaticDnsServers, loaded.Snapshot.StaticDnsServers);
            Assert.Equal(ServerIp, loaded.ServerIp);
            Assert.Equal("Wi-Fi", loaded.DisabledAdapters.Single().Name);

            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Damaged_backup_file_gives_plain_english_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prodhcp-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var ex = Assert.Throws<AdapterOperationException>(() => new FileSessionStore(path).Load());
            Assert.Contains("damaged", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Snapshot_differences_ignore_addresses_when_dhcp()
    {
        var a = new AdapterSnapshot { AdapterId = "x", InterfaceIndex = 1, AdapterName = "x", DhcpEnabled = true };
        var b = a with { StaticAddresses = [new(IPv4.Parse("10.0.0.9"), 24)] };
        Assert.Empty(AdapterSessionManager.SnapshotDifferences(a, b));
        Assert.Single(AdapterSessionManager.SnapshotDifferences(a, a with { DhcpEnabled = false }));
    }
}
