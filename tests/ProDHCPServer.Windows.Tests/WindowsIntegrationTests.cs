using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows.Tests;

/// <summary>Runs only on Windows (the CI runner). Never changes adapter settings.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>Needs administrator rights (GitHub's Windows runners have them).</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
        else if (!Elevation.IsAdministrator()) Skip = "Needs administrator rights";
    }
}

[SupportedOSPlatform("windows")]
public class WindowsIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The machine's main IPv4 adapter: up, has a gateway, not loopback.</summary>
    private static (AdapterInfo Adapter, AddressAssignment Address) PrimaryAdapter()
    {
        var control = new WindowsAdapterControl();
        var adapter = control.ListAdapters().First(a => a.MediaConnected && a.Gateways.Count > 0 && a.Addresses.Any(x => !IPv4.IsApipa(x.Address)));
        return (adapter, adapter.Addresses.First(x => !IPv4.IsApipa(x.Address)));
    }

    [WindowsFact]
    public void Lists_adapters_with_index_link_and_addresses()
    {
        var all = new WindowsAdapterControl().ListAdapters();
        Assert.NotEmpty(all);
        var (primary, address) = PrimaryAdapter();
        Assert.True(primary.Index > 0);
        Assert.NotEqual(MacAddress.Empty, primary.Mac);
        Assert.True(primary.SpeedBitsPerSecond > 0);
        Assert.InRange(address.PrefixLength, 1, 32);
        Assert.True(primary.BytesReceived > 0, "Expected interface byte counters on the primary adapter.");
        Assert.NotNull(primary.BytesSent);
        output.WriteLine($"{primary.Name}: {primary.Description}, driver {primary.Driver?.ToString() ?? "(not found)"} [{primary.Driver?.HardwareId}], rx {primary.BytesReceived} tx {primary.BytesSent}");
        Assert.NotNull(primary.Driver);
    }

    [WindowsFact]
    public void Captures_persisted_settings_from_registry()
    {
        var (primary, _) = PrimaryAdapter();
        var snapshot = new WindowsAdapterControl().Capture(primary.Id);
        Assert.Equal(primary.Index, snapshot.InterfaceIndex);
        Assert.Equal(primary.DhcpEnabled, snapshot.DhcpEnabled);
        if (!snapshot.DhcpEnabled) Assert.NotEmpty(snapshot.StaticAddresses);
    }

    [AdminFact]
    public void Neighbor_entry_struct_layout_is_accepted_by_windows()
    {
        var (primary, address) = PrimaryAdapter();
        // An unused address in the adapter's subnet, near the top of the range.
        var ip = IPv4.FromUInt32(IPv4.ToUInt32(IPv4.BroadcastAddress(address.Address, address.PrefixLength)) - 3);
        var mac = MacAddress.Parse("02:00:5E:10:20:30");
        var table = new NeighborTable();
        try
        {
            Assert.Null(table.Set(primary.Index, ip, mac));
            var shown = CommandRunner.Run("netsh.exe", ["interface", "ipv4", "show", "neighbors", $"interface={primary.Index}"]).Output;
            Assert.Contains(ip.ToString(), shown);
            Assert.Contains("02-00-5e-10-20-30", shown, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            table.Clear();
        }
        var after = CommandRunner.Run("netsh.exe", ["interface", "ipv4", "show", "neighbors", $"interface={primary.Index}"]).Output;
        Assert.DoesNotContain("02-00-5e-10-20-30", after, StringComparison.OrdinalIgnoreCase);
    }

    [AdminFact]
    public async Task Server_socket_receives_and_answers_on_the_real_stack()
    {
        var (primary, address) = PrimaryAdapter();
        var prefix = Math.Max(address.PrefixLength, 16);
        var net = IPv4.ToUInt32(IPv4.NetworkAddress(address.Address, prefix));
        var bcast = IPv4.ToUInt32(IPv4.BroadcastAddress(address.Address, prefix));
        var config = new DhcpServerConfig
        {
            ServerIp = address.Address,
            PrefixLength = prefix,
            PoolStart = IPv4.FromUInt32(bcast - 20),
            PoolEnd = IPv4.FromUInt32(bcast - 10),
        };
        var engine = new DhcpServerEngine(config);
        var events = new List<DhcpEngineEvent>();
        engine.EventRaised += e => { lock (events) events.Add(e); };

        await using var server = new DhcpSocketServer(engine, primary.Index);
        try
        {
            server.Start();
        }
        catch (TransportBindException ex) when (ex.Message.Contains("in use"))
        {
            output.WriteLine($"NOT RUN: {ex.Message}");
            return; // Something on the runner already owns port 67; nothing to test here.
        }

        // A client on this host, with a working address, sends INFORM: the reply is unicast back to ciaddr:68.
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Bind(new IPEndPoint(address.Address, DhcpPorts.Client));
        var inform = new DhcpPacket { TransactionId = 0xC0FFEE, ClientIp = address.Address };
        MacAddress.Parse("02:00:5E:AA:BB:CC").ToBytes().CopyTo(inform.ClientHardwareAddress, 0);
        inform.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)DhcpMessageType.Inform));
        client.SendTo(inform.Serialize(), new IPEndPoint(address.Address, DhcpPorts.Server));

        var buffer = new byte[2048];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        DhcpPacket? reply = null;
        while (reply is null)
        {
            var r = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            var p = DhcpPacket.Parse(buffer.AsSpan(0, r.ReceivedBytes));
            if (p.Op == BootOp.Reply && p.TransactionId == 0xC0FFEE) reply = p;
        }

        output.WriteLine($"INFORM answered on {primary.Name} (index {primary.Index}) {address}: {reply.Summary()}");
        Assert.Equal(DhcpMessageType.Ack, reply.MessageType);
        Assert.Equal(config.ServerIp, reply.ServerIdentifier);
        Assert.Null(reply.GetOption(DhcpOptionCode.LeaseTime));
        lock (events) Assert.Contains(events, e => e.Kind == DhcpEventKind.Received && e.MessageType == DhcpMessageType.Inform);
    }

    [AdminFact]
    public async Task Server_socket_receives_a_broadcast_discover()
    {
        // Real clients have no IP and send DISCOVER to 255.255.255.255. Check a socket bound to the
        // server's own address still gets limited broadcasts (Windows loops them back locally).
        var (primary, address) = PrimaryAdapter();
        var prefix = Math.Max(address.PrefixLength, 16);
        var bcast = IPv4.ToUInt32(IPv4.BroadcastAddress(address.Address, prefix));
        var config = new DhcpServerConfig
        {
            ServerIp = address.Address,
            PrefixLength = prefix,
            PoolStart = IPv4.FromUInt32(bcast - 20),
            PoolEnd = IPv4.FromUInt32(bcast - 10),
        };
        var engine = new DhcpServerEngine(config);
        var received = new TaskCompletionSource<DhcpEngineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EventRaised += e =>
        {
            if (e.Kind == DhcpEventKind.Received && e.MessageType == DhcpMessageType.Discover) received.TrySetResult(e);
        };
        var problems = new List<string>();

        await using var server = new DhcpSocketServer(engine, primary.Index);
        server.Problem += p => { lock (problems) problems.Add(p.Message); };
        try
        {
            server.Start();
        }
        catch (TransportBindException ex) when (ex.Message.Contains("in use"))
        {
            output.WriteLine($"NOT RUN: {ex.Message}");
            return;
        }

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(primary.Index));
        client.Bind(new IPEndPoint(address.Address, 0));
        var discover = new DhcpPacket { TransactionId = 0xB0CA57, Flags = 0x8000 };
        MacAddress.Parse("02:00:5E:AA:BB:CD").ToBytes().CopyTo(discover.ClientHardwareAddress, 0);
        discover.Options.Add(DhcpOption.FromByte(DhcpOptionCode.MessageType, (byte)DhcpMessageType.Discover));
        var bytes = discover.Serialize();
        for (int i = 0; i < 3 && !received.Task.IsCompleted; i++)
        {
            client.SendTo(bytes, new IPEndPoint(IPAddress.Broadcast, DhcpPorts.Server));
            await Task.WhenAny(received.Task, Task.Delay(1000));
        }

        lock (problems) foreach (var p in problems) output.WriteLine($"Problem: {p}");
        Assert.True(received.Task.IsCompleted, "The server socket never received the broadcast DISCOVER.");
        output.WriteLine($"Broadcast DISCOVER received on {primary.Name} (index {primary.Index}) {address}.");
    }

    [AdminFact]
    public void Firewall_rule_can_be_added_and_removed()
    {
        Firewall.Allow();
        var shown = CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", $"name={Firewall.RuleName}"]);
        Assert.Contains("67,68", shown.Output);
        var discovery = CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", $"name={Firewall.DiscoveryRuleName}"]);
        Assert.Contains(Firewall.DiscoveryRuleName, discovery.Output);
        Firewall.Remove();
        var discoveryGone = CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", $"name={Firewall.DiscoveryRuleName}"]);
        Assert.DoesNotContain("Enabled:", discoveryGone.Output);
        var gone = CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", $"name={Firewall.RuleName}"]);
        Assert.DoesNotContain("67,68", gone.Output);
    }

    [WindowsFact]
    public void Port_owner_lookup_names_a_process()
    {
        using var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)holder.Client.LocalEndPoint!).Port;
        var text = PortOwner.Describe(port);
        Assert.NotNull(text);
        Assert.Contains($"process {Environment.ProcessId}", text);
    }

    [WindowsFact]
    public async Task Ping_and_port_probe_work_against_localhost()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (open, _) = await DeviceProber.TcpOpen(IPAddress.Loopback, port, CancellationToken.None);
            Assert.True(open);
        }
        finally { listener.Stop(); }

        using var ping = new Ping();
        Assert.Equal(IPStatus.Success, (await ping.SendPingAsync(IPAddress.Loopback, 1000)).Status);
    }

    [WindowsFact]
    public async Task Device_monitor_pings_once_a_second_and_logs_nothing_while_answering()
    {
        var log = new Core.SessionLog();
        await using var monitor = new DeviceMonitor(log);
        var mac = MacAddress.Parse("02:00:00:00:00:01");
        monitor.Sync([new MonitoredDevice(mac, IPAddress.Loopback, "Loopback")]);
        await Task.Delay(TimeSpan.FromSeconds(3.5));

        var history = monitor.History(mac);
        Assert.NotNull(history);
        var samples = history.Snapshot();
        output.WriteLine(string.Join(", ", samples.Select(s => $"{s.Success}/{s.RoundTripMs}")));
        Assert.InRange(samples.Count, 3, 5);
        Assert.All(samples, s => Assert.True(s.Success));
        Assert.DoesNotContain(log.Snapshot(), e => e.Category == Core.LogCategory.Device);

        // Dropping the device stops its pinger.
        monitor.Sync([]);
        Assert.Null(monitor.History(mac));
    }

    [WindowsFact]
    public void Arp_table_reads_and_resolves_the_gateway()
    {
        var (adapter, address) = PrimaryAdapter();
        var gateway = adapter.Gateways[0];
        var mac = WindowsArp.Probe(gateway, address.Address);
        output.WriteLine($"Gateway {gateway} is at {mac?.ToString() ?? "no answer"}");
        var entries = WindowsArp.Read(adapter.Index);
        output.WriteLine(string.Join(Environment.NewLine, entries.Select(e => $"{e.Ip} {e.Mac}")));
        Assert.All(entries, e => Assert.Equal(adapter.Index, e.InterfaceIndex));
        if (mac is not null) Assert.Contains(entries, e => e.Ip.Equals(gateway));
    }

    [WindowsFact]
    public void Npcap_check_never_throws()
    {
        output.WriteLine($"Npcap installed: {NpcapCapture.IsInstalled}");
        if (!NpcapCapture.IsInstalled) Assert.Equal("Npcap isn't installed.", new NpcapCapture("{00000000-0000-0000-0000-000000000000}", _ => { }).Start());
    }

    [AdminFact]
    public async Task Discovery_socket_and_raw_sniffer_receive_a_pjlink_reply()
    {
        var (_, address) = PrimaryAdapter();
        var received = new TaskCompletionSource<Core.Discovery.Sighting>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sniffed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var socket = DiscoverySocket.Open(address.Address, 0, [], 1, (from, port, buffer, length) =>
        {
            if (Core.Discovery.VendorProtocols.TryParse(DateTimeOffset.Now, from.Address, 4352, port, buffer.AsSpan(0, length)) is { } s) received.TrySetResult(s);
        });
        await using var sniffer = new RawIpSniffer(address.Address, (buffer, length) =>
        {
            if (Core.Discovery.IPv4Packet.TryParse(buffer.AsSpan(0, length), out var ip) && ip.TryGetUdp(out _, out int dp, out _) && dp == socket.LocalPort) sniffed.TrySetResult(true);
        });
        var problem = sniffer.Start();
        output.WriteLine($"Sniffer: {problem ?? "listening"}");

        using var sender = new UdpClient(new IPEndPoint(address.Address, 0));
        var reply = "%2ACKN=00:0b:a2:11:22:33\r"u8.ToArray();
        for (int i = 0; i < 5 && !received.Task.IsCompleted; i++)
        {
            await sender.SendAsync(reply, new IPEndPoint(address.Address, socket.LocalPort));
            await Task.WhenAny(received.Task, Task.Delay(500));
        }
        var sighting = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PJLink", sighting.Label);
        Assert.Equal(MacAddress.Parse("00:0B:A2:11:22:33"), sighting.Mac);
        if (problem is null) output.WriteLine($"Sniffer saw it: {await Task.WhenAny(sniffed.Task, Task.Delay(2000)) == sniffed.Task}");
    }
}
