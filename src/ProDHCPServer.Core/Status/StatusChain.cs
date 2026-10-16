using System.Net;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;

namespace ProDHCPServer.Core.Status;

public enum StepState
{
    /// <summary>Not applicable yet (e.g. server not started). Drawn grey.</summary>
    Idle,
    Good,
    Warning,
    Bad,
}

public enum StepId
{
    Adapter = 1,
    Link,
    ServerIp,
    Server,
    Traffic,
    Lease,
    Ping,
    WebUi,
}

public sealed record StatusStep(StepId Id, string Title, StepState State, string Reason, string? Action = null, string? Detail = null);

public sealed record ProbeResult(DateTimeOffset At, bool Success, double? RoundTripMs = null, string? Detail = null);

public sealed record WebUiProbe(DateTimeOffset At, bool HttpOpen, bool HttpsOpen, string? Detail = null);

/// <summary>Everything the status chain looks at, captured at one moment.</summary>
public sealed record StatusInputs
{
    public DateTimeOffset Now { get; init; }
    public AdapterInfo? Adapter { get; init; }
    public DhcpServerConfig Config { get; init; } = DhcpServerConfig.Default;

    /// <summary>Start has been pressed and not yet stopped.</summary>
    public bool SessionActive { get; init; }
    public DateTimeOffset? SessionStartedAt { get; init; }
    /// <summary>When the link last came up (null while down).</summary>
    public DateTimeOffset? LinkUpSince { get; init; }
    /// <summary>Set when the adapter has received nothing for a while although this laptop kept sending.</summary>
    public TimeSpan? ReceiveStalledFor { get; init; }

    public bool ServerListening { get; init; }
    public DateTimeOffset? ListeningSince { get; init; }
    public string? ServerError { get; init; }
    public string? ServerErrorDetail { get; init; }
    /// <summary>The probe is running, before the server listens.</summary>
    public bool CheckingForOtherServers { get; init; }
    public ForeignDhcpServer? OtherServer { get; init; }
    public bool OtherServerOverridden { get; init; }

    public IReadOnlyList<DhcpEngineEvent> Traffic { get; init; } = [];
    public IReadOnlyList<Lease> Leases { get; init; } = [];
    /// <summary>The device the user is focused on; defaults to the newest bound lease.</summary>
    public Lease? FocusLease { get; init; }

    public ProbeResult? Ping { get; init; }
    public int ConsecutivePingFailures { get; init; }
    public WebUiProbe? WebUi { get; init; }

    /// <summary>Other adapters whose subnet overlaps ours (e.g. Wi-Fi on 192.168.1.0/24).</summary>
    public IReadOnlyList<AdapterInfo> ConflictingAdapters { get; init; } = [];
    public IReadOnlyList<AdapterInfo> ConnectedVpns { get; init; } = [];
}

/// <summary>
/// Turns raw state into the eight green/amber/red steps, each with a plain-English reason and
/// what to do next. Pure: every message here is covered by unit tests.
/// </summary>
public static class StatusChain
{
    public static readonly TimeSpan NoTrafficWarning = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OfferIgnoredWarning = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<StatusStep> Evaluate(StatusInputs s) =>
    [
        AdapterStep(s),
        LinkStep(s),
        ServerIpStep(s),
        ServerStep(s),
        TrafficStep(s),
        LeaseStep(s),
        PingStep(s),
        WebUiStep(s),
    ];

    /// <summary>The single most useful line for the headline: the first step that isn't green.</summary>
    public static StatusStep Headline(IReadOnlyList<StatusStep> steps)
    {
        var bad = steps.FirstOrDefault(x => x.State == StepState.Bad);
        if (bad is not null) return bad;
        var warn = steps.FirstOrDefault(x => x.State == StepState.Warning);
        if (warn is not null) return warn;
        var idle = steps.FirstOrDefault(x => x.State == StepState.Idle);
        if (idle is not null) return idle;
        return steps[^1];
    }

    private static StatusStep AdapterStep(StatusInputs s)
    {
        const string title = "Adapter selected";
        var a = s.Adapter;
        if (a is null)
            return new(StepId.Adapter, title, StepState.Bad, "No adapter selected.", "Pick the Ethernet port your cable is plugged into.");
        if (!a.IsEnabled)
            return new(StepId.Adapter, title, StepState.Bad, $"\"{a.Name}\" is disabled in Windows.", "Enable it in Network Connections, or pick another adapter.");
        return a.Kind switch
        {
            AdapterKind.Vpn => new(StepId.Adapter, title, StepState.Bad, $"\"{a.Name}\" is a VPN adapter, not a physical port.", "Pick the Ethernet adapter instead."),
            AdapterKind.WiFi => new(StepId.Adapter, title, StepState.Warning, $"\"{a.Name}\" is Wi-Fi. Serving DHCP over Wi-Fi is unusual.", "Pick the Ethernet adapter unless you really mean Wi-Fi."),
            AdapterKind.Virtual => new(StepId.Adapter, title, StepState.Warning, $"\"{a.Name}\" looks like a virtual adapter ({a.Description}).", "Pick a physical Ethernet adapter."),
            _ => PhysicalAdapterStep(s, a, title),
        };
    }

    private static StatusStep PhysicalAdapterStep(StatusInputs s, AdapterInfo a, string title)
    {
        var detail = $"MAC {a.Mac}, interface index {a.Index}";
        if (s.ConflictingAdapters.FirstOrDefault() is { } clash)
            return new(StepId.Adapter, title, StepState.Warning,
                $"{a.Name} is selected, but \"{clash.Name}\" is also on {clash.AddressSummary}, which overlaps {IPv4Cidr(s)}. Windows may send device traffic the wrong way.",
                $"Use \"Turn off {clash.Name} until Stop\", or pick a profile on a different subnet.", detail);
        if (s.ConnectedVpns.FirstOrDefault() is { } vpn)
            return new(StepId.Adapter, title, StepState.Warning,
                $"{a.Name} is selected. A VPN (\"{vpn.Name}\") is connected; some VPNs block local traffic.",
                "If the device doesn't respond, disconnect the VPN.", detail);
        if (a.Driver is { } driver) detail += $", driver {driver}";
        if (AdapterRules.KnownDriverProblem(a) is { } driverProblem && s.Ping?.Success != true)
            return new(StepId.Adapter, title, StepState.Warning, $"{a.Name} is selected. {driverProblem}", UpdateDriverAction, detail);
        return new(StepId.Adapter, title, StepState.Good, $"{a.Name} ({a.Description})", Detail: detail);
    }

    private const string UpdateDriverAction =
        "Install the newer driver: Windows Settings > Windows Update > Advanced options > Optional updates > Driver updates, tick the Realtek one only. Or use a USB adapter with a different chip.";

    private static string IPv4Cidr(StatusInputs s) => Net.IPv4.ToCidr(s.Config.ServerIp, s.Config.PrefixLength);

    private static StatusStep LinkStep(StatusInputs s)
    {
        const string title = "Cable / link";
        var a = s.Adapter;
        if (a is null) return new(StepId.Link, title, StepState.Idle, "Waiting for an adapter.");
        if (!a.MediaConnected)
            return new(StepId.Link, title, StepState.Bad, "No link. Windows sees no cable connected.",
                "Check the cable is seated at both ends, the device is powered on, and try a known-good cable or a different port.");
        if (s.ReceiveStalledFor is { } quiet)
        {
            var secs = (int)quiet.TotalSeconds;
            var fix = AdapterRules.KnownDriverProblem(a) is not null
                ? $"Unplug the USB network adapter from the laptop and plug it back in. To stop it happening again: {UpdateDriverAction}"
                : "Unplug the USB network adapter from the laptop and plug it back in. If it keeps happening, update the adapter's driver or use a different adapter.";
            if (s.FocusLease is not null || s.Leases.Any(l => l.State == LeaseState.Bound))
                return new(StepId.Link, title, StepState.Bad,
                    $"The cable shows connected, but {a.Name} has received nothing for {secs} s while this laptop kept sending. The adapter has probably stopped receiving.", fix);
            return new(StepId.Link, title, StepState.Warning,
                $"The cable shows connected, but {a.Name} has received nothing for {secs} s. Either the device isn't sending anything, or the adapter has stopped receiving.", fix);
        }
        var speed = $"{a.SpeedText}, {a.DuplexText}";
        if (a.FullDuplex == false || a.SpeedBitsPerSecond is > 0 and <= 10_000_000)
            return new(StepId.Link, title, StepState.Warning, $"Link up at {speed}. That is slower than expected.",
                "Usually a damaged cable or a speed/duplex mismatch. Try another cable; check the device isn't forced to 10 Mbps or half duplex.");
        return new(StepId.Link, title, StepState.Good, $"Link up at {speed}.");
    }

    private static StatusStep ServerIpStep(StatusInputs s)
    {
        const string title = "My static IP";
        var a = s.Adapter;
        var cidr = $"{s.Config.ServerIp}/{s.Config.PrefixLength}";
        if (a is null || !s.SessionActive)
        {
            var current = a is null ? "" : $" Currently: {a.AddressSummary}.";
            return new(StepId.ServerIp, title, StepState.Idle, $"Will be set to {cidr} when you press Start.{current}");
        }
        if (a.DuplicateAddresses.Any(d => d.Equals(s.Config.ServerIp)))
            return new(StepId.ServerIp, title, StepState.Bad, $"Another device on the cable is already using {s.Config.ServerIp}.",
                "Stop, change the server IP (or use a different profile), then Start again.");
        if (a.HasAddress(s.Config.ServerIp))
        {
            var others = a.Addresses.Where(x => !x.Address.Equals(s.Config.ServerIp)).ToList();
            var detail = others.Count > 0 ? $"Also on this adapter: {string.Join(", ", others)}" : null;
            return new(StepId.ServerIp, title, StepState.Good, $"{cidr} is set on {a.Name}.", Detail: detail);
        }
        if (!a.MediaConnected)
            return new(StepId.ServerIp, title, StepState.Warning, $"{cidr} is saved, but Windows only applies it once a cable is connected.",
                "Plug in the cable. This turns green by itself when the link comes up.");
        if (a.HasOnlyApipa)
            return new(StepId.ServerIp, title, StepState.Bad, $"Windows is still on a self-assigned address ({a.AddressSummary}) instead of {cidr}.",
                "Wait a few seconds. If it stays red, press Stop then Start.");
        return new(StepId.ServerIp, title, StepState.Warning, $"Waiting for Windows to apply {cidr} (currently {a.AddressSummary}).",
            "This normally takes a few seconds after the link comes up. If it stays amber, press Stop then Start.");
    }

    private static StatusStep ServerStep(StatusInputs s)
    {
        const string title = "DHCP server";
        if (s.OtherServer is { } other && !s.OtherServerOverridden)
        {
            var via = other.Router is { } r ? $", gateway {r}" : "";
            return new(StepId.Server, title, StepState.Bad,
                $"Another DHCP server is already on this link ({other.ServerId}, offering {other.OfferedIp}{via}). You may be plugged into a venue or office network.",
                "Unplug from the network and connect straight to the device. Only use \"Serve anyway\" if you are sure.",
                other.Detail);
        }
        if (s.ServerError is { } error)
            return new(StepId.Server, title, StepState.Bad, error, Detail: s.ServerErrorDetail);
        if (!s.SessionActive)
            return new(StepId.Server, title, StepState.Idle, "Stopped. Press Start.");
        if (s.CheckingForOtherServers)
            return new(StepId.Server, title, StepState.Warning, "Checking that no other DHCP server is on this link…");
        if (!s.ServerListening)
            return new(StepId.Server, title, StepState.Warning, "Waiting for the static IP before listening on port 67.");
        var pool = $"pool {s.Config.PoolStart} to {s.Config.PoolEnd}";
        var note = s.OtherServer is not null ? " Another DHCP server was detected and overridden." : "";
        return new(StepId.Server, title, s.OtherServer is null ? StepState.Good : StepState.Warning,
            $"Listening on {s.Config.ServerIp}:67, {pool}.{note}");
    }

    private static StatusStep TrafficStep(StatusInputs s)
    {
        const string title = "DHCP traffic";
        if (!s.SessionActive || !s.ServerListening)
            return new(StepId.Traffic, title, StepState.Idle, "Waiting for the server to start.");

        var t = s.Traffic;
        DateTimeOffset? Last(DhcpEventKind kind, DhcpMessageType? type) =>
            t.LastOrDefault(e => e.Kind == kind && (type is null || e.MessageType == type))?.Time;

        var discover = Last(DhcpEventKind.Received, DhcpMessageType.Discover);
        var request = Last(DhcpEventKind.Received, DhcpMessageType.Request);
        var offer = Last(DhcpEventKind.Sent, DhcpMessageType.Offer);
        var ack = Last(DhcpEventKind.Sent, DhcpMessageType.Ack);
        var nak = Last(DhcpEventKind.Sent, DhcpMessageType.Nak);
        var bootp = t.LastOrDefault(e => e.Kind == DhcpEventKind.LeaseBound && e.MessageType is null)?.Time;
        var timeline = Timeline(("DISCOVER", discover), ("OFFER", offer), ("REQUEST", request), ("ACK", ack), ("NAK", nak));
        var detail = string.Join(Environment.NewLine, t.TakeLast(12).Select(e => $"{e.Time:HH:mm:ss.fff}  {e.Message}"));

        if (t.LastOrDefault(e => e.Kind == DhcpEventKind.OtherServerChosen) is { } chose)
            return new(StepId.Traffic, title, StepState.Bad, chose.Message,
                "Another DHCP server is answering this device. Make sure you're connected only to the device.", detail);
        if (t.LastOrDefault(e => e.Kind == DhcpEventKind.PoolExhausted) is { } full && full.Time > (ack ?? DateTimeOffset.MinValue))
            return new(StepId.Traffic, title, StepState.Bad, full.Message, "Widen the pool range or clear old leases.", detail);

        if (ack is not null || bootp is not null)
            return new(StepId.Traffic, title, StepState.Good, timeline.Length > 0 ? timeline : "BOOTP reply sent.", Detail: detail);

        if (discover is null && request is null)
        {
            var since = Max(s.ListeningSince, s.LinkUpSince);
            var waited = since is { } x ? s.Now - x : TimeSpan.Zero;
            if (s.Adapter is { MediaConnected: false })
                return new(StepId.Traffic, title, StepState.Warning, "No link, so no DHCP requests can arrive.", "Connect the cable.");
            if (waited >= NoTrafficWarning)
                return new(StepId.Traffic, title, StepState.Warning,
                    $"Link is up but no DHCP requests in {waited.TotalSeconds:0} s. The device may have a static IP or DHCP turned off.",
                    "Power-cycle the device so it asks again, or check its manual for its default static IP.", detail);
            return new(StepId.Traffic, title, StepState.Warning, $"Listening… no requests yet ({waited.TotalSeconds:0} s).",
                "Most devices ask for an address within a few seconds of link-up or power-on.");
        }

        if (offer is not null && request is null && s.Now - offer.Value >= OfferIgnoredWarning)
            return new(StepId.Traffic, title, StepState.Bad,
                $"The device keeps sending DISCOVER but never accepts the OFFER. {timeline}",
                "It may not be hearing the reply. Turn on \"Always broadcast replies\" in Settings, and check Windows Firewall isn't blocking this app.", detail);

        if (nak is not null)
            return new(StepId.Traffic, title, StepState.Warning, $"Told the device to start over (NAK). {timeline}",
                "Normal if it remembered an address from another network; it should DISCOVER again within seconds.", detail);

        return new(StepId.Traffic, title, StepState.Warning, $"Talking to the device… {timeline}", Detail: detail);
    }

    private static StatusStep LeaseStep(StatusInputs s)
    {
        const string title = "Device leased an IP";
        if (!s.SessionActive)
            return new(StepId.Lease, title, StepState.Idle, "No devices yet.");
        var bound = s.Leases.Where(l => l.State == LeaseState.Bound && l.Expires > s.Now).ToList();
        var declined = s.Traffic.LastOrDefault(e => e.Kind == DhcpEventKind.LeaseDeclined);
        if (bound.Count == 0)
        {
            if (declined is not null)
                return new(StepId.Lease, title, StepState.Bad, declined.Message,
                    "Find the device that has that static IP, or narrow the pool so it isn't handed out.");
            return new(StepId.Lease, title, StepState.Idle, "No device has an address yet.");
        }
        var focus = s.FocusLease ?? bound.MaxBy(l => l.BoundAt);
        var name = focus!.HostName is { Length: > 0 } h ? $"{h} " : "";
        var more = bound.Count > 1 ? $" ({bound.Count} devices in total)" : "";
        var state = declined is not null ? StepState.Warning : StepState.Good;
        return new(StepId.Lease, title, state, $"{name}{focus.Mac} has {focus.Ip}{more}.",
            declined is not null ? declined.Message : null);
    }

    private static StatusStep PingStep(StatusInputs s)
    {
        const string title = "Device responding (ping)";
        var focus = FocusedBound(s);
        if (focus is null) return new(StepId.Ping, title, StepState.Idle, "Waiting for a device to get an address.");
        if (s.Ping is null) return new(StepId.Ping, title, StepState.Warning, $"Pinging {focus.Ip}…");
        if (s.Ping.Success)
            return new(StepId.Ping, title, StepState.Good, $"{focus.Ip} replies in {Ms(s.Ping.RoundTripMs)}.");
        var webUp = s.WebUi is { } w && (w.HttpOpen || w.HttpsOpen);
        if (webUp)
            return new(StepId.Ping, title, StepState.Warning, $"{focus.Ip} doesn't answer ping, but its web server does. It probably just blocks ping.");
        if (s.ReceiveStalledFor is not null)
            return new(StepId.Ping, title, StepState.Bad, $"No ping reply from {focus.Ip}, because nothing at all is being received on this adapter.",
                "See the Cable / link step: the adapter has stopped receiving.", s.Ping.Detail);
        if (s.ConsecutivePingFailures >= 10)
            return new(StepId.Ping, title, StepState.Bad, $"{focus.Ip} has an address but isn't answering ping.",
                "It may still be booting. If it stays red, a firewall on the device may block ping; check the WebUI step.", s.Ping.Detail);
        return new(StepId.Ping, title, StepState.Warning, $"No ping reply from {focus.Ip} yet. Devices often answer only once fully booted.", Detail: s.Ping.Detail);
    }

    private static StatusStep WebUiStep(StatusInputs s)
    {
        const string title = "WebUI reachable";
        var focus = FocusedBound(s);
        if (focus is null) return new(StepId.WebUi, title, StepState.Idle, "Waiting for a device to get an address.");
        if (s.WebUi is null) return new(StepId.WebUi, title, StepState.Warning, $"Checking ports 80 and 443 on {focus.Ip}…");
        var w = s.WebUi;
        if (w.HttpOpen && w.HttpsOpen)
            return new(StepId.WebUi, title, StepState.Good, $"HTTP (80) and HTTPS (443) are open on {focus.Ip}.", Detail: w.Detail);
        if (w.HttpsOpen) return new(StepId.WebUi, title, StepState.Good, $"HTTPS (443) is open on {focus.Ip}.", Detail: w.Detail);
        if (w.HttpOpen) return new(StepId.WebUi, title, StepState.Good, $"HTTP (80) is open on {focus.Ip}.", Detail: w.Detail);
        return new(StepId.WebUi, title, StepState.Warning, $"Nothing answering on port 80 or 443 at {focus.Ip}.",
            "The device may still be booting, have no WebUI, or use another port (try the 8080 and 8443 buttons, or the vendor's app).", w.Detail);
    }

    private static Lease? FocusedBound(StatusInputs s)
    {
        if (!s.SessionActive) return null;
        if (s.FocusLease is { State: LeaseState.Bound } f && f.Expires > s.Now) return f;
        return s.Leases.Where(l => l.State == LeaseState.Bound && l.Expires > s.Now).MaxBy(l => l.BoundAt);
    }

    private static string Timeline(params (string Name, DateTimeOffset? At)[] items) =>
        string.Join("  →  ", items.Where(i => i.At is not null).OrderBy(i => i.At).Select(i => $"{i.Name} {i.At:HH:mm:ss}"));

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    private static string Ms(double? ms) => ms is null ? "?" : ms < 1 ? "<1 ms" : $"{ms:0} ms";
}
