using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.Core.Tests;

public class StatusChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static AdapterInfo Eth(Func<AdapterInfo, AdapterInfo>? tweak = null)
    {
        var a = new AdapterInfo
        {
            Id = "eth", Index = 7, Name = "Ethernet", Description = "Intel I219", Kind = AdapterKind.Ethernet,
            MediaConnected = true, SpeedBitsPerSecond = 1_000_000_000, FullDuplex = true,
            Addresses = [new(Packets.Server, 24)],
        };
        return tweak is null ? a : tweak(a);
    }

    private static StatusInputs Running(Func<StatusInputs, StatusInputs>? tweak = null)
    {
        var s = new StatusInputs
        {
            Now = T0.AddSeconds(5),
            Adapter = Eth(),
            SessionActive = true,
            SessionStartedAt = T0,
            LinkUpSince = T0,
            ServerListening = true,
            ListeningSince = T0,
        };
        return tweak is null ? s : tweak(s);
    }

    private static StatusStep Step(StatusInputs s, StepId id) => StatusChain.Evaluate(s).Single(x => x.Id == id);

    private static DhcpEngineEvent Ev(int secs, DhcpEventKind kind, DhcpMessageType? type, string msg = "") =>
        new(T0.AddSeconds(secs), kind, Packets.MacA, type, null, msg);

    private static Lease BoundLease(string? host = "dsp-1") => new()
    {
        Mac = Packets.MacA, Ip = IPv4.Parse("192.168.100.100"), State = LeaseState.Bound, HostName = host,
        FirstSeen = T0, LastSeen = T0, BoundAt = T0, Expires = T0.AddHours(1),
    };

    [Fact]
    public void Evaluates_eight_steps_in_order()
    {
        var steps = StatusChain.Evaluate(new StatusInputs { Now = T0 });
        Assert.Equal(Enum.GetValues<StepId>(), steps.Select(s => s.Id));
    }

    [Fact]
    public void No_adapter_is_red_and_says_what_to_pick()
    {
        var step = Step(new StatusInputs { Now = T0 }, StepId.Adapter);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("Ethernet port", step.Action);
    }

    [Fact]
    public void Vpn_adapter_is_red_and_wifi_is_amber()
    {
        Assert.Equal(StepState.Bad, Step(Running(s => s with { Adapter = Eth(a => a with { Kind = AdapterKind.Vpn }) }), StepId.Adapter).State);
        Assert.Equal(StepState.Warning, Step(Running(s => s with { Adapter = Eth(a => a with { Kind = AdapterKind.WiFi }) }), StepId.Adapter).State);
    }

    [Fact]
    public void Overlapping_wifi_is_flagged_with_the_fix()
    {
        var wifi = Eth(a => a with { Id = "wifi", Name = "Wi-Fi", Kind = AdapterKind.WiFi, Addresses = [new(IPv4.Parse("192.168.100.23"), 24)] });
        var step = Step(Running(s => s with { ConflictingAdapters = [wifi] }), StepId.Adapter);
        Assert.Equal(StepState.Warning, step.State);
        Assert.Contains("Turn off Wi-Fi until Stop", step.Action);
    }

    [Fact]
    public void Unplugged_cable_is_red()
    {
        var step = Step(Running(s => s with { Adapter = Eth(a => a with { MediaConnected = false }) }), StepId.Link);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("No link", step.Reason);
    }

    [Fact]
    public void Gigabit_full_duplex_is_green_with_speed_shown()
    {
        var step = Step(Running(), StepId.Link);
        Assert.Equal(StepState.Good, step.State);
        Assert.Equal("Link up at 1 Gbps, full duplex.", step.Reason);
    }

    [Fact]
    public void Ten_meg_half_duplex_is_amber()
    {
        var step = Step(Running(s => s with { Adapter = Eth(a => a with { SpeedBitsPerSecond = 10_000_000, FullDuplex = false }) }), StepId.Link);
        Assert.Equal(StepState.Warning, step.State);
        Assert.Contains("10 Mbps, half duplex", step.Reason);
    }

    [Fact]
    public void Static_ip_while_unplugged_explains_windows_behaviour()
    {
        var step = Step(Running(s => s with { Adapter = Eth(a => a with { MediaConnected = false, Addresses = [] }) }), StepId.ServerIp);
        Assert.Equal(StepState.Warning, step.State);
        Assert.Contains("once a cable is connected", step.Reason);
    }

    [Fact]
    public void Apipa_instead_of_static_is_red()
    {
        var step = Step(Running(s => s with { Adapter = Eth(a => a with { Addresses = [new(IPv4.Parse("169.254.10.2"), 16)] }) }), StepId.ServerIp);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("self-assigned", step.Reason);
    }

    [Fact]
    public void Duplicate_server_ip_is_red()
    {
        var step = Step(Running(s => s with { Adapter = Eth(a => a with { DuplicateAddresses = [Packets.Server] }) }), StepId.ServerIp);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("already using 192.168.100.1", step.Reason);
    }

    [Fact]
    public void Idle_before_start_shows_what_will_happen()
    {
        var step = Step(new StatusInputs { Now = T0, Adapter = Eth() }, StepId.ServerIp);
        Assert.Equal(StepState.Idle, step.State);
        Assert.Contains("192.168.100.1/24 when you press Start", step.Reason);
    }

    [Fact]
    public void Other_dhcp_server_is_red_and_loud()
    {
        var other = new ForeignDhcpServer(IPv4.Parse("10.0.0.1"), null, IPv4.Parse("10.0.0.88"), IPv4.Parse("10.0.0.1"), "detail");
        var step = Step(Running(s => s with { OtherServer = other, ServerListening = false }), StepId.Server);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("Another DHCP server", step.Reason);
        Assert.Contains("venue", step.Reason);
        Assert.Equal("detail", step.Detail);

        var overridden = Step(Running(s => s with { OtherServer = other, OtherServerOverridden = true }), StepId.Server);
        Assert.Equal(StepState.Warning, overridden.State);
    }

    [Fact]
    public void Server_error_is_shown_verbatim()
    {
        var step = Step(Running(s => s with { ServerError = "Port 67 is in use by Internet Connection Sharing.", ServerErrorDetail = "WSAEADDRINUSE" }), StepId.Server);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Equal("WSAEADDRINUSE", step.Detail);
    }

    [Fact]
    public void No_requests_after_30s_suggests_static_ip()
    {
        var step = Step(Running(s => s with { Now = T0.AddSeconds(31) }), StepId.Traffic);
        Assert.Equal(StepState.Warning, step.State);
        Assert.Contains("no DHCP requests in 31 s", step.Reason);
        Assert.Contains("static IP", step.Reason);
    }

    [Fact]
    public void Under_30s_is_just_listening()
    {
        var step = Step(Running(s => s with { Now = T0.AddSeconds(10) }), StepId.Traffic);
        Assert.Contains("no requests yet (10 s)", step.Reason);
    }

    [Fact]
    public void Ignored_offers_point_at_broadcast_setting()
    {
        var step = Step(Running(s => s with
        {
            Now = T0.AddSeconds(30),
            Traffic = [Ev(1, DhcpEventKind.Received, DhcpMessageType.Discover), Ev(1, DhcpEventKind.Sent, DhcpMessageType.Offer)],
        }), StepId.Traffic);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Contains("Always broadcast replies", step.Action);
    }

    [Fact]
    public void Completed_dora_is_green_with_timeline()
    {
        var step = Step(Running(s => s with
        {
            Traffic =
            [
                Ev(1, DhcpEventKind.Received, DhcpMessageType.Discover), Ev(1, DhcpEventKind.Sent, DhcpMessageType.Offer),
                Ev(2, DhcpEventKind.Received, DhcpMessageType.Request), Ev(2, DhcpEventKind.Sent, DhcpMessageType.Ack),
            ],
        }), StepId.Traffic);
        Assert.Equal(StepState.Good, step.State);
        Assert.Equal("DISCOVER 09:00:01  →  OFFER 09:00:01  →  REQUEST 09:00:02  →  ACK 09:00:02", step.Reason);
    }

    [Fact]
    public void Device_choosing_other_server_is_red()
    {
        var step = Step(Running(s => s with { Traffic = [Ev(1, DhcpEventKind.OtherServerChosen, DhcpMessageType.Request, "picked 10.0.0.1")] }), StepId.Traffic);
        Assert.Equal(StepState.Bad, step.State);
        Assert.Equal("picked 10.0.0.1", step.Reason);
    }

    [Fact]
    public void Lease_step_names_the_device()
    {
        var step = Step(Running(s => s with { Leases = [BoundLease()] }), StepId.Lease);
        Assert.Equal(StepState.Good, step.State);
        Assert.Equal("dsp-1 00:1D:C1:11:22:33 has 192.168.100.100.", step.Reason);
    }

    [Fact]
    public void Decline_without_lease_is_red()
    {
        var step = Step(Running(s => s with { Traffic = [Ev(1, DhcpEventKind.LeaseDeclined, DhcpMessageType.Decline, "conflict")] }), StepId.Lease);
        Assert.Equal(StepState.Bad, step.State);
    }

    [Fact]
    public void Ping_failure_with_open_web_port_is_only_amber()
    {
        var step = Step(Running(s => s with
        {
            Leases = [BoundLease()],
            Ping = new ProbeResult(T0, false),
            ConsecutivePingFailures = 20,
            WebUi = new WebUiProbe(T0, true, false),
        }), StepId.Ping);
        Assert.Equal(StepState.Warning, step.State);
        Assert.Contains("blocks ping", step.Reason);
    }

    [Fact]
    public void Persistent_ping_failure_is_red()
    {
        var step = Step(Running(s => s with
        {
            Leases = [BoundLease()],
            Ping = new ProbeResult(T0, false),
            ConsecutivePingFailures = 10,
            WebUi = new WebUiProbe(T0, false, false),
        }), StepId.Ping);
        Assert.Equal(StepState.Bad, step.State);
    }

    [Fact]
    public void Ping_success_shows_latency()
    {
        var step = Step(Running(s => s with { Leases = [BoundLease()], Ping = new ProbeResult(T0, true, 0.4) }), StepId.Ping);
        Assert.Equal("192.168.100.100 replies in <1 ms.", step.Reason);
    }

    [Theory]
    [InlineData(true, false, StepState.Good, "HTTP (80)")]
    [InlineData(false, true, StepState.Good, "HTTPS (443)")]
    [InlineData(false, false, StepState.Warning, "Nothing answering")]
    public void WebUi_step_reports_ports(bool http, bool https, StepState expected, string text)
    {
        var step = Step(Running(s => s with { Leases = [BoundLease()], WebUi = new WebUiProbe(T0, http, https) }), StepId.WebUi);
        Assert.Equal(expected, step.State);
        Assert.Contains(text, step.Reason);
    }

    [Fact]
    public void Headline_is_first_red_step()
    {
        var steps = StatusChain.Evaluate(Running(s => s with { Adapter = Eth(a => a with { MediaConnected = false }) }));
        Assert.Equal(StepId.Link, StatusChain.Headline(steps).Id);
    }

    [Fact]
    public void Headline_is_webui_when_everything_is_green()
    {
        var steps = StatusChain.Evaluate(Running(s => s with
        {
            Leases = [BoundLease()],
            Traffic = [Ev(1, DhcpEventKind.Sent, DhcpMessageType.Ack)],
            Ping = new ProbeResult(T0, true, 1),
            WebUi = new WebUiProbe(T0, true, true),
        }));
        Assert.All(steps, s => Assert.Equal(StepState.Good, s.State));
        Assert.Equal(StepId.WebUi, StatusChain.Headline(steps).Id);
    }
}
