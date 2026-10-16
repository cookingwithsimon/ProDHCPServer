using CommunityToolkit.Mvvm.ComponentModel;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Devices;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.App.ViewModels;

public sealed partial class StepViewModel : ObservableObject
{
    public StepViewModel(StepId id) => Id = id;

    public StepId Id { get; }
    public int Number => (int)Id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string? _action;
    [ObservableProperty] private string? _detail;

    public void Update(StatusStep step)
    {
        Title = step.Title;
        State = step.State;
        Reason = step.Reason;
        Action = step.Action;
        Detail = step.Detail;
    }

    /// <summary>Forces the state colour to be looked up again (after a theme change).</summary>
    public void Repaint() => OnPropertyChanged(nameof(State));
}

public sealed partial class AdapterItemViewModel : ObservableObject
{
    public AdapterItemViewModel(AdapterInfo info) => Info = info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Subtitle), nameof(LinkText), nameof(LinkState), nameof(KindText))]
    private AdapterInfo _info;

    public string Id => Info.Id;
    public string Name => Info.Name;
    public string KindText => Info.Kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Virtual => "Virtual",
        _ => "Other",
    };
    public string Subtitle => $"{KindText} · {Info.Description}";
    public string LinkText => Info.MediaConnected ? $"Link up {Info.SpeedText} · {Info.AddressSummary}" : $"No link · {Info.AddressSummary}";
    public StepState LinkState => Info.MediaConnected ? StepState.Good : StepState.Idle;
}

public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(MacAddress mac) => Mac = mac;

    public MacAddress Mac { get; }
    public string MacText => Mac.ToString();

    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private string? _hostName;
    [ObservableProperty] private string? _vendor;
    [ObservableProperty] private string? _vendorClass;
    [ObservableProperty] private LeaseState _leaseState;
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private string _firstSeenText = "";
    [ObservableProperty] private bool _isFocused;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private StepState _stateColour;
    [ObservableProperty] private IReadOnlyList<ProbeResult>? _pingSamples;
    /// <summary>Short, for the device list: "3 ms" or "No reply".</summary>
    [ObservableProperty] private string _pingText = "";
    /// <summary>For the focused device: the last minute's latency and loss.</summary>
    [ObservableProperty] private string _pingSummary = "";
    [ObservableProperty] private StepState _pingState;

    public void UpdatePing(PingHistory? history, DateTimeOffset now)
    {
        if (history?.Last is not { } last)
        {
            PingSamples = null;
            PingText = LeaseState == LeaseState.Bound ? "Pinging…" : "";
            PingSummary = PingText;
            PingState = StepState.Idle;
            return;
        }
        PingSamples = history.Snapshot();
        var minute = history.Stats(now, TimeSpan.FromMinutes(1));
        PingText = last.Success && last.RoundTripMs is { } ms
            ? minute.Lost > 0 ? $"{PingStats.Ms(ms)}, {minute.LossPercent:0}% loss" : PingStats.Ms(ms)
            : "No reply";
        PingSummary = $"Last minute: {minute.Describe()}";
        PingState = last.Success
            ? minute.Lost > 0 ? StepState.Warning : StepState.Good
            : history.ConsecutiveFailures >= PingHistory.DownAfter ? StepState.Bad : StepState.Warning;
    }

    public void Update(Lease lease, DateTimeOffset now)
    {
        Ip = lease.Ip.ToString();
        HostName = lease.HostName;
        Vendor ??= OuiDatabase.Default.Lookup(lease.Mac) ?? "Unknown manufacturer";
        VendorClass = lease.VendorClass;
        LeaseState = lease.State;
        Title = lease.HostName is { Length: > 0 } h ? h : Vendor ?? "Device";
        LeaseText = lease.State switch
        {
            LeaseState.Bound when lease.IsBootp => "BOOTP address",
            LeaseState.Bound => $"Lease {Duration(lease.Expires - now)} left",
            LeaseState.Offered => "Offered, waiting for the device to accept",
            LeaseState.Released => "Released by the device",
            _ => "Lease expired",
        };
        FirstSeenText = $"First seen {Ago(now - lease.FirstSeen)} ago ({lease.FirstSeen:HH:mm:ss})";
        StateColour = lease.State switch
        {
            LeaseState.Bound => StepState.Good,
            LeaseState.Offered => StepState.Warning,
            _ => StepState.Idle,
        };
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public static string Ago(TimeSpan t) => Duration(t);
}

public sealed class LogItemViewModel(LogEntry entry)
{
    public LogEntry Entry { get; } = entry;
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public LogSeverity Severity => Entry.Severity;
    public string Category => LogExport.CategoryName(Entry.Category);
    public string Message => Entry.Message;
    public string? Detail => Entry.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Entry.Detail);
}
