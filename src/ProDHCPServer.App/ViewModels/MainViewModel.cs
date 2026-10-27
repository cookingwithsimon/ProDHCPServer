using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Profiles;
using ProDHCPServer.Core.Status;
using ProDHCPServer.Windows;

namespace ProDHCPServer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogRows = 600;
    private readonly IAdapterControl _adapters;
    private readonly AdapterSessionManager _sessions;
    private readonly ServerController _controller;
    private readonly SettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private AppSettings _settings;
    private bool _refreshing;
    private IReadOnlyList<AdapterInfo> _allAdapters = [];
    private DeviceProber? _prober;
    private readonly DeviceMonitor _monitor;
    private MacAddress? _userFocus;

    public MainViewModel(IAdapterControl adapters, AdapterSessionManager sessions, ServerController controller,
        SessionLog log, SettingsStore settingsStore, ProfileLibrary profiles, Dispatcher dispatcher)
    {
        _adapters = adapters;
        _sessions = sessions;
        _controller = controller;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        Log = log;
        _settings = settingsStore.Load();
        _monitor = new DeviceMonitor(log);
        if (profiles.Load(_settings) is { } profileProblem) log.Add(LogSeverity.Warning, LogCategory.App, profileProblem);
        Settings = new SettingsViewModel(profiles, _settings.ActiveProfile, _settings.Theme, Theme.Find(_settings.Look));
        Settings.ActiveProfileChanged += profile =>
        {
            if (_settings.ActiveProfile != profile.Name)
            {
                _settings = _settings with { ActiveProfile = profile.Name };
                SaveSettings();
            }
            UpdateStatus();
        };
        Settings.ThemeChanged += theme =>
        {
            _settings = _settings with { Theme = theme };
            SaveSettings();
            ApplyTheme();
        };
        Settings.LookChanged += look =>
        {
            _settings = _settings with { Look = look.Id };
            SaveSettings();
            ApplyTheme();
        };
        Steps = new ObservableCollection<StepViewModel>(Enum.GetValues<StepId>().Select(id => new StepViewModel(id)));

        RebuildLog();
        if (log.Snapshot().LastOrDefault() is { } last) LatestLogText = $"{last.Time:HH:mm:ss}  {last.Message}";
        log.Added += entry => _dispatcher.BeginInvoke(() =>
        {
            LatestLogText = $"{entry.Time:HH:mm:ss}  {entry.Message}";
            _logTotal++;
            if (CurrentLogFilter.Matches(entry))
            {
                LogRows.Add(new LogItemViewModel(entry));
                while (LogRows.Count > MaxLogRows) LogRows.RemoveAt(0);
            }
            UpdateLogCount();
        });

        PendingRecovery = SafeFindRecovery();

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public SessionLog Log { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<AdapterItemViewModel> Adapters { get; } = [];
    public ObservableCollection<StepViewModel> Steps { get; }
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<LogItemViewModel> LogRows { get; } = [];
    public ObservableCollection<FoundDeviceViewModel> Found { get; } = [];

    // ---------- static IP finder and discovery ----------

    [ObservableProperty] private string _foundCountText = "";
    [ObservableProperty] private string? _switchPortText;
    [ObservableProperty] private string? _switchPortDetail;
    [ObservableProperty] private string? _discoveryNote;
    [ObservableProperty] private bool _npcapMissing;
    [ObservableProperty] private string? _sweepStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
    private bool _isSweeping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private AdapterItemViewModel? _selectedAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(SettingsEditable))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private bool _isRunning;

    partial void OnIsRunningChanged(bool value)
    {
        Settings.IsLocked = value;
        SweepCommand.NotifyCanExecuteChanged();
    }

    // ---------- session log filter ----------

    public static IReadOnlyList<LogLevelOption> LogLevels { get; } =
    [
        new(LogLevelFilter.All, "Everything"),
        new(LogLevelFilter.WarningsAndErrors, "Warnings and problems"),
        new(LogLevelFilter.ErrorsOnly, "Problems only"),
    ];

    public static IReadOnlyList<LogAreaOption> LogAreas { get; } =
        [new(null, "All areas"), .. Enum.GetValues<LogCategory>().Select(c => new LogAreaOption(c, LogExport.CategoryName(c)))];

    [ObservableProperty] private LogLevelOption _logLevel = LogLevels[0];
    [ObservableProperty] private LogAreaOption _logArea = LogAreas[0];
    [ObservableProperty] private string _logSearch = "";
    [ObservableProperty] private string _logCountText = "";
    private int _logTotal;

    partial void OnLogLevelChanged(LogLevelOption value) => RebuildLog();
    partial void OnLogAreaChanged(LogAreaOption value) => RebuildLog();
    partial void OnLogSearchChanged(string value) => RebuildLog();

    private LogFilter CurrentLogFilter => new(LogLevel.Level, LogArea.Category, LogSearch);

    private void RebuildLog()
    {
        var all = Log.Snapshot();
        _logTotal = all.Count;
        var filter = CurrentLogFilter;
        LogRows.Clear();
        foreach (var entry in all.Where(filter.Matches).TakeLast(MaxLogRows)) LogRows.Add(new LogItemViewModel(entry));
        UpdateLogCount();
    }

    private void UpdateLogCount()
    {
        if (CurrentLogFilter.IsEmpty) { LogCountText = _logTotal == 1 ? "1 entry" : $"{_logTotal} entries"; return; }
        var shown = Log.Snapshot().Count(CurrentLogFilter.Matches);
        LogCountText = $"{shown} of {_logTotal} entries match";
    }

    [RelayCommand]
    private void ClearLogFilter()
    {
        LogLevel = LogLevels[0];
        LogArea = LogAreas[0];
        LogSearch = "";
    }

    /// <summary>Saves the entries the filter shows (all of them, not just the last few hundred on screen) as TXT or CSV.</summary>
    [RelayCommand]
    private void ExportLog()
    {
        var now = DateTimeOffset.Now;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export session log",
            Filter = "Text for reading or a job report (*.txt)|*.txt|CSV for Excel (*.csv)|*.csv",
            FileName = LogExport.SuggestedFileName(now, "txt"),
            AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var filter = CurrentLogFilter;
        var entries = Log.Snapshot().Where(filter.Matches).ToList();
        bool csv = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        try
        {
            // Excel only reads UTF-8 CSV correctly with a byte order mark.
            if (csv) File.WriteAllText(dialog.FileName, LogExport.ToCsv(entries), new UTF8Encoding(true));
            else File.WriteAllText(dialog.FileName, LogExport.ToText(entries, ExportHeader(filter), now), new UTF8Encoding(false));
            Log.Add(LogSeverity.Info, LogCategory.App, $"Exported {entries.Count} log entries to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save the log to {dialog.FileName}: {ex.Message}", ex.ToString());
        }
    }

    private IEnumerable<string> ExportHeader(LogFilter filter)
    {
        yield return $"ProDHCPServer {App.Version} on {Environment.MachineName} (Windows {Environment.OSVersion.Version})";
        if (SelectedAdapter?.Info is { } a)
            yield return $"Adapter: {a.Name} ({a.Description}){(a.Driver is { } d ? $", driver {d}" : "")}";
        var p = Settings.ActiveProfile;
        yield return $"Profile: {p.Name} ({p.Summary}, lease {p.LeaseMinutes} min)";
        if (_controller.StartedAt is { } started) yield return $"Server started {started:yyyy-MM-dd HH:mm:ss}{(_controller.IsActive ? ", still running" : "")}";
        if (!filter.IsEmpty)
            yield return $"Filter: {LogLevel.Label}, {LogArea.Label}{(string.IsNullOrWhiteSpace(LogSearch) ? "" : $", containing \"{LogSearch.Trim()}\"")}";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _headlineText = "";
    [ObservableProperty] private string? _headlineAction;
    [ObservableProperty] private StepState _headlineState;

    [ObservableProperty] private DeviceViewModel? _focusedDevice;
    [ObservableProperty] private string _deviceCountText = "";
    [ObservableProperty] private string _webUiHint = "";

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _errorDetail;
    [ObservableProperty] private bool _canRetryRestore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveryText))]
    private SessionRecord? _pendingRecovery;

    [ObservableProperty] private string? _otherServerText;
    [ObservableProperty] private string? _otherServerDetail;
    [ObservableProperty] private string? _conflictText;
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private string _latestLogText = "";

    private AdapterInfo? _conflictingAdapter;

    public string StartStopText => IsBusy ? (IsRunning ? "Stopping…" : "Starting…") : IsRunning ? "Stop server" : "Start server";
    public bool SettingsEditable => !IsRunning;

    public string RecoveryText => PendingRecovery is { } r
        ? $"ProDHCPServer didn't shut down cleanly last time (started {r.StartedAt:g}). \"{r.Snapshot.AdapterName}\" may still be set to {r.ServerIp}. Put it back to its original settings: {r.Snapshot.Describe()}?"
        : "";

    private SessionRecord? SafeFindRecovery()
    {
        try { return _sessions.FindPendingRecovery(); }
        catch (AdapterOperationException ex)
        {
            ShowError(ex.Message, ex.Detail);
            return null;
        }
    }

    // ---------- refresh loop ----------

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            IReadOnlyList<AdapterInfo> list;
            try
            {
                list = await Task.Run(_adapters.ListAdapters);
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't read the network adapters: {ex.Message}", ex.ToString());
                return;
            }
            _allAdapters = list;
            UpdateAdapterList(list);
            IsRunning = _controller.IsActive;
            UpdateDevices();
            UpdateFound();
            UpdateStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateAdapterList(IReadOnlyList<AdapterInfo> list)
    {
        var ordered = AdapterRules.PickerOrder(list).ToList();
        foreach (var gone in Adapters.Where(a => ordered.All(o => o.Id != a.Id)).ToList())
        {
            // Keep the selected adapter visible (as disconnected) if it vanishes mid-session.
            if (gone == SelectedAdapter) { gone.Info = gone.Info with { MediaConnected = false, Addresses = [] }; continue; }
            Adapters.Remove(gone);
        }
        for (int i = 0; i < ordered.Count; i++)
        {
            var existing = Adapters.FirstOrDefault(a => a.Id == ordered[i].Id);
            if (existing is null) Adapters.Insert(Math.Min(i, Adapters.Count), new AdapterItemViewModel(ordered[i]));
            else if (existing.Info != ordered[i]) existing.Info = ordered[i];
        }

        if (SelectedAdapter is null && Adapters.Count > 0)
        {
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == _settings.LastAdapterId)
                ?? Adapters.FirstOrDefault(a => a.Info is { Kind: AdapterKind.Ethernet, MediaConnected: true })
                ?? Adapters.FirstOrDefault(a => a.Info.Kind == AdapterKind.Ethernet)
                ?? Adapters[0];
        }
    }

    partial void OnSelectedAdapterChanged(AdapterItemViewModel? value)
    {
        if (value is null || IsRunning) return;
        if (_settings.LastAdapterId != value.Id)
        {
            _settings = _settings with { LastAdapterId = value.Id };
            SaveSettings();
        }
        UpdateStatus();
    }

    private void UpdateDevices()
    {
        var engine = _controller.Engine;
        var now = DateTimeOffset.Now;
        var leases = engine?.GetLeases() ?? [];
        foreach (var lease in leases)
        {
            var vm = Devices.FirstOrDefault(d => d.Mac == lease.Mac);
            if (vm is null) { vm = new DeviceViewModel(lease.Mac); Devices.Add(vm); }
            vm.Update(lease, now);
        }
        foreach (var gone in Devices.Where(d => leases.All(l => l.Mac != d.Mac)).ToList()) Devices.Remove(gone);

        var bound = leases.Where(l => l.State == LeaseState.Bound && l.Expires > now).ToList();
        _monitor.Sync(bound.Select(l => new MonitoredDevice(l.Mac, l.Ip, Devices.First(d => d.Mac == l.Mac).Title)).ToList());
        foreach (var d in Devices) d.UpdatePing(bound.Any(l => l.Mac == d.Mac) ? _monitor.History(d.Mac) : null, now);
        var focusLease = (_userFocus is { } f ? bound.FirstOrDefault(l => l.Mac == f) : null) ?? bound.MaxBy(l => l.BoundAt);
        var focusVm = focusLease is null ? null : Devices.First(d => d.Mac == focusLease.Mac);
        foreach (var d in Devices) d.IsFocused = d == focusVm;
        FocusedDevice = focusVm;
        DeviceCountText = Devices.Count switch
        {
            0 => "",
            1 => "1 device",
            var n => $"{n} devices",
        };

        var target = focusLease?.Ip;
        if (!Equals(_prober?.Target, target))
        {
            var old = _prober;
            _prober = target is null ? null : new DeviceProber(target);
            if (old is not null) _ = old.DisposeAsync().AsTask();
        }
        WebUiHint = _prober?.LastWebUi switch
        {
            null when target is not null => "Checking ports 80 and 443…",
            { HttpOpen: true, HttpsOpen: true } => "HTTP and HTTPS are open",
            { HttpsOpen: true } => "HTTPS (443) is open",
            { HttpOpen: true } => "HTTP (80) is open",
            { } => "No web server found on 80 or 443 yet",
            _ => "",
        };
    }

    private void UpdateStatus()
    {
        var adapter = SelectedAdapter?.Info;
        var (config, _) = Settings.BuildActive();
        config = _controller.Config ?? config ?? DhcpServerConfig.Default;
        var engine = _controller.Engine;
        var conflicts = adapter is null ? [] : AdapterRules.OverlappingAdapters(_allAdapters, adapter.Id, config.ServerIp, config.PrefixLength);
        var vpns = AdapterRules.ConnectedVpns(_allAdapters).Where(v => v.Id != adapter?.Id).ToList();
        var leases = engine?.GetLeases() ?? [];
        var focus = FocusedDevice is null ? null : leases.FirstOrDefault(l => l.Mac == FocusedDevice.Mac);

        var inputs = new StatusInputs
        {
            Now = DateTimeOffset.Now,
            Adapter = adapter,
            Config = config,
            SessionActive = _controller.IsActive,
            SessionStartedAt = _controller.StartedAt,
            LinkUpSince = _controller.LinkUpSince,
            ReceiveStalledFor = _controller.ReceiveStalledFor,
            ServerListening = _controller.Listening,
            ListeningSince = _controller.ListeningSince,
            ServerError = _controller.ServerError,
            ServerErrorDetail = _controller.ServerErrorDetail,
            CheckingForOtherServers = _controller.CheckingForOtherServers,
            OtherServer = _controller.OtherServer,
            OtherServerOverridden = _controller.OtherServerOverridden,
            Traffic = DhcpTraffic(),
            Leases = leases,
            FocusLease = focus,
            Ping = FocusedPing?.Last,
            ConsecutivePingFailures = FocusedPing?.ConsecutiveFailures ?? 0,
            WebUi = _prober?.LastWebUi,
            ConflictingAdapters = conflicts,
            ConnectedVpns = vpns,
            FoundOnOtherSubnets = Found.Where(f => !f.IsReachable && f.Device is not null).Select(f => f.Device!).ToList(),
        };

        var steps = StatusChain.Evaluate(inputs);
        foreach (var step in steps) Steps.First(s => s.Id == step.Id).Update(step);

        var head = StatusChain.Headline(steps);
        if (steps.All(s => s.State == StepState.Good) && FocusedDevice is { } d)
        {
            HeadlineState = StepState.Good;
            HeadlineText = $"All good: {d.Title} is at {d.Ip}";
            HeadlineAction = "Press Open WebUI to connect.";
        }
        else if (!_controller.IsActive && head.State != StepState.Bad)
        {
            HeadlineState = StepState.Idle;
            HeadlineText = adapter is null ? "Pick an adapter to begin" : $"Ready to serve DHCP on {adapter.Name}";
            HeadlineAction = $"Press Start to set {config.ServerIp}/{config.PrefixLength} and hand out {config.PoolStart} to {config.PoolEnd} (profile \"{Settings.ActiveProfile.Name}\").";
        }
        else
        {
            HeadlineState = head.State;
            HeadlineText = head.Reason;
            HeadlineAction = head.Action;
        }

        _conflictingAdapter = conflicts.FirstOrDefault();
        ConflictText = _conflictingAdapter is { } c && _controller.IsActive
            ? $"\"{c.Name}\" is on {c.AddressSummary}, which overlaps the DHCP subnet. Traffic to the device may go out the wrong adapter."
            : null;

        if (_controller.OtherServer is { } other && !_controller.OtherServerOverridden)
        {
            OtherServerText = $"Another DHCP server is on this link: {other.ServerId} offered {other.OfferedIp}. You may be plugged into a venue or office network, so ProDHCPServer is not serving.";
            OtherServerDetail = other.Detail;
        }
        else
        {
            OtherServerText = null;
            OtherServerDetail = null;
        }
    }

    private void UpdateFound()
    {
        var discovery = _controller.Discovery;
        var local = SelectedAdapter?.Info.Addresses ?? [];
        var leases = _controller.Engine?.GetLeases() ?? [];
        var list = discovery.Running
            ? discovery.Table.Snapshot().Where(d => !leases.Any(l => l.Mac == d.Mac || l.Ip.Equals(d.Ip))).ToList()
            : [];
        foreach (var gone in Found.Where(f => list.All(d => d.Id != f.Id)).ToList()) Found.Remove(gone);
        for (int i = 0; i < list.Count; i++)
        {
            var vm = Found.FirstOrDefault(f => f.Id == list[i].Id);
            if (vm is null) { vm = new FoundDeviceViewModel(list[i].Id); Found.Insert(Math.Min(i, Found.Count), vm); }
            else if (Found.IndexOf(vm) != i) Found.Move(Found.IndexOf(vm), Math.Min(i, Found.Count - 1));
            vm.Update(list[i], local);
        }
        int elsewhere = Found.Count(f => !f.IsReachable);
        FoundCountText = Found.Count switch
        {
            0 => "",
            var n => $"{n} {(n == 1 ? "device" : "devices")}{(elsewhere > 0 ? $", {elsewhere} on another subnet" : "")}",
        };
        SwitchPortText = discovery.SwitchPort?.Describe();
        SwitchPortDetail = discovery.SwitchPort?.DetailText();
        NpcapMissing = discovery.Running && !discovery.NpcapAvailable;
        DiscoveryNote = discovery.Running && discovery.SwitchPort is null && !NpcapMissing
            ? "No switch port information yet. Managed switches send it about every 30 seconds (LLDP or CDP)."
            : null;
        SweepStatus = discovery.SweepStatus;
        IsSweeping = discovery.Sweeping;
    }

    private bool CanSweep() => IsRunning && !IsSweeping;

    /// <summary>Scans the likely AV subnets for devices with static IPs, using a temporary address on each.</summary>
    [RelayCommand(CanExecute = nameof(CanSweep))]
    private void Sweep()
    {
        _controller.Discovery.StartSweep(_allAdapters);
        UpdateFound();
    }

    /// <summary>Adds an address on the device's subnet (until Stop) so its WebUI can be opened.</summary>
    [RelayCommand]
    private async Task Reach(FoundDeviceViewModel? found)
    {
        if (found?.Device is not { } device || found.IsBusy) return;
        if (SelectedAdapter?.Info is not { } adapter) return;
        int prefix = Core.Discovery.SubnetPlanner.ReachPrefix(device.Ip);
        var clash = AdapterRules.OverlappingAdapters(_allAdapters, adapter.Id, device.Ip, prefix).FirstOrDefault();
        if (clash is not null)
        {
            var ok = ConfirmDialog.Show(Application.Current.MainWindow,
                title: $"Turn off \"{clash.Name}\" until Stop?",
                message: $"\"{clash.Name}\" is on {clash.AddressSummary}, the same subnet as {device.Ip}. To reach the device on the cable, " +
                         $"\"{clash.Name}\" has to be off. It's turned back on when you press Stop.",
                confirmText: "Turn it off and continue");
            if (!ok) return;
            try { await Task.Run(() => _sessions.DisableForSession(clash)); Log.Add(LogSeverity.Info, LogCategory.Adapter, $"Turned off \"{clash.Name}\" until Stop."); }
            catch (Exception ex)
            {
                ShowError($"Couldn't turn off \"{clash.Name}\": {ex.Message}", (ex as AdapterOperationException)?.Detail ?? ex.ToString());
                return;
            }
        }
        found.IsBusy = true;
        try
        {
            await _controller.Discovery.ReachAsync(device);
            DismissError();
        }
        catch (AdapterOperationException ex)
        {
            ShowError(ex.Message, ex.Detail);
            Log.Add(LogSeverity.Error, LogCategory.Adapter, ex.Message, ex.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError($"Couldn't reach {device.Ip}: {ex.Message}", ex.ToString());
        }
        finally
        {
            found.IsBusy = false;
        }
        await RefreshAsync();
    }

    /// <summary>Opens a found device's web page: the one it advertised, else whichever of 80 and 443 answers.</summary>
    [RelayCommand]
    private async Task OpenFound(FoundDeviceViewModel? found)
    {
        if (found?.Device is not { } device) return;
        if (!found.IsReachable) { ShowError($"Press Reach it first: this laptop isn't on {device.Ip}'s subnet yet."); return; }
        if (device.WebPage is { } page) { OpenUrl(page.ToString()); return; }
        found.IsBusy = true;
        try
        {
            var http = DeviceProber.TcpOpen(device.Ip, 80, CancellationToken.None);
            var https = DeviceProber.TcpOpen(device.Ip, 443, CancellationToken.None);
            var (h, hs) = (await http, await https);
            if (h.Open) OpenUrl($"http://{device.Ip}/");
            else if (hs.Open) OpenUrl($"https://{device.Ip}/");
            else ShowError($"{device.Ip} has no web page on port 80 or 443. It may only be set up with the maker's software.",
                $"Port 80: {h.Detail}{Environment.NewLine}Port 443: {hs.Detail}");
        }
        finally
        {
            found.IsBusy = false;
        }
    }

    [RelayCommand]
    private void GetNpcap() => OpenUrl("https://npcap.com/#download");

    private Core.Devices.PingHistory? FocusedPing => FocusedDevice is { } d ? _monitor.History(d.Mac) : null;

    private IReadOnlyList<DhcpEngineEvent> _trafficCache = [];
    private int _trafficCount = -1;

    private IReadOnlyList<DhcpEngineEvent> DhcpTraffic()
    {
        var entries = _engineEvents;
        lock (entries)
        {
            if (entries.Count != _trafficCount) { _trafficCache = entries.ToList(); _trafficCount = entries.Count; }
        }
        return _trafficCache;
    }

    private readonly List<DhcpEngineEvent> _engineEvents = [];

    /// <summary>Wired by the window: keeps the DHCP events of the current session for the status chain.</summary>
    public void OnDhcpEvent(DhcpEngineEvent e)
    {
        lock (_engineEvents)
        {
            _engineEvents.Add(e);
            if (_engineEvents.Count > 2000) _engineEvents.RemoveRange(0, 1000);
        }
    }

    // ---------- commands ----------

    private bool CanStartStop() => !IsBusy && (IsRunning || SelectedAdapter is not null);

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private async Task StartStop()
    {
        if (IsRunning) await StopAsync();
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        DismissError();
        if (PendingRecovery is not null)
        {
            ShowError("Restore or dismiss the leftover adapter backup (the banner at the top) before starting.");
            return;
        }
        if (SelectedAdapter?.Info is not { } adapter)
        {
            ShowError("Pick the adapter your cable is plugged into first.");
            return;
        }
        // Unsaved edits to the profile are saved first, the same as pressing Save.
        if (!Settings.SaveIfDirty())
        {
            SettingsOpen = true;
            ShowError($"The profile \"{Settings.ActiveProfile.Name}\" has changes that need fixing before starting.", Settings.Errors);
            return;
        }
        var (config, problems) = Settings.BuildActive();
        if (config is null)
        {
            SettingsOpen = true;
            ShowError($"The profile \"{Settings.ActiveProfile.Name}\" needs fixing before starting.", string.Join(Environment.NewLine, problems));
            return;
        }

        lock (_engineEvents) _engineEvents.Clear();
        Devices.Clear();
        _userFocus = null;
        IsBusy = true;
        try
        {
            await Task.Run(() => _controller.Start(adapter, config));
            Log.Add(LogSeverity.Info, LogCategory.Server, $"Using profile \"{Settings.ActiveProfile.Name}\" ({Settings.ActiveProfile.Summary}).");
            IsRunning = true;
        }
        catch (AdapterOperationException ex)
        {
            ShowError(ex.Message, ex.Detail);
            Log.Add(LogSeverity.Error, LogCategory.Adapter, ex.Message, ex.Detail);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't start: {ex.Message}", ex.ToString());
            Log.Add(LogSeverity.Error, LogCategory.App, $"Couldn't start: {ex.Message}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    public async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Task.Run(_controller.StopAsync);
            if (!result.Success)
            {
                ShowError(result.Summary, string.Join(Environment.NewLine, result.Problems));
                CanRetryRestore = true;
            }
        }
        finally
        {
            IsBusy = false;
            IsRunning = _controller.IsActive;
        }
        if (_prober is { } p) { _prober = null; await p.DisposeAsync(); }
        await _monitor.ClearAsync();
        Found.Clear();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RetryRestore()
    {
        var result = await Task.Run(_sessions.End);
        if (result.Success)
        {
            DismissError();
            Log.Add(LogSeverity.Success, LogCategory.Adapter, result.Summary);
        }
        else ShowError(result.Summary, string.Join(Environment.NewLine, result.Problems));
        CanRetryRestore = !result.Success;
    }

    [RelayCommand]
    private async Task RestorePending()
    {
        if (PendingRecovery is not { } record) return;
        var result = await Task.Run(() => _sessions.Recover(record));
        Log.Add(result.Success ? LogSeverity.Success : LogSeverity.Error, LogCategory.Adapter, result.Summary,
            result.Problems.Count > 0 ? string.Join(Environment.NewLine, result.Problems) : null);
        if (result.Success) PendingRecovery = null;
        else ShowError(result.Summary, string.Join(Environment.NewLine, result.Problems));
    }

    [RelayCommand]
    private void DiscardPending()
    {
        _sessions.DiscardPendingRecovery();
        Log.Add(LogSeverity.Warning, LogCategory.Adapter, "Kept the adapter's current settings and discarded the old backup.");
        PendingRecovery = null;
    }

    [RelayCommand]
    private void ServeAnyway()
    {
        var other = _controller.OtherServer?.ServerId.ToString() ?? "the other server";
        var confirmed = ConfirmDialog.Show(
            Application.Current.MainWindow,
            title: $"Serve DHCP alongside {other}?",
            message: $"{other} is already handing out addresses on this network. Serving as well can give wrong addresses to other people's equipment. " +
                     "Only do this if you are connected just to your own devices.",
            confirmText: "Serve anyway");
        if (confirmed) _controller.OverrideOtherServer();
    }

    [RelayCommand]
    private async Task TurnOffConflicting()
    {
        if (_conflictingAdapter is not { } other) return;
        try
        {
            await Task.Run(() => _sessions.DisableForSession(other));
            Log.Add(LogSeverity.Info, LogCategory.Adapter, $"Turned off \"{other.Name}\" until Stop.");
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't turn off \"{other.Name}\": {ex.Message}", (ex as AdapterOperationException)?.Detail ?? ex.ToString());
        }
    }

    [RelayCommand]
    private void FocusDevice(DeviceViewModel? device)
    {
        if (device is null) return;
        _userFocus = device.Mac;
        UpdateDevices();
        UpdateStatus();
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (FocusedDevice is not { } d) return;
        var web = _prober?.LastWebUi;
        // Prefer http when both are open: devices that want https redirect, and it avoids a certificate warning first.
        var scheme = web is { HttpOpen: false, HttpsOpen: true } ? "https" : "http";
        OpenUrl($"{scheme}://{d.Ip}/");
    }

    [RelayCommand]
    private void OpenPort(string? spec)
    {
        if (FocusedDevice is not { } d || string.IsNullOrEmpty(spec)) return;
        var parts = spec.Split(':');
        OpenUrl($"{parts[0]}://{d.Ip}:{parts[1]}/");
    }

    [RelayCommand]
    private void CopyIp(DeviceViewModel? device)
    {
        var ip = (device ?? FocusedDevice)?.Ip;
        if (ip is null) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(ip); Log.Add(LogSeverity.Info, LogCategory.App, $"Copied {ip} to the clipboard."); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
        ShowError("Couldn't copy to the clipboard (another program is holding it). Try again.");
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private void ToggleSettings() => SettingsOpen = !SettingsOpen;

    [RelayCommand]
    private void CloseSettings() => SettingsOpen = false;

    /// <summary>Checks the settings form when a field loses focus, so problems show before Save rather than while typing.</summary>
    [RelayCommand]
    private void ValidateSettings() => Settings.Validate();

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
        ErrorDetail = null;
    }

    [RelayCommand]
    private async Task RefreshAdapters() => await RefreshAsync();

    public void ApplyTheme()
    {
        Theme.Apply(_settings.Theme, _settings.Look);
        foreach (var s in Steps) s.Repaint();
        OnPropertyChanged(nameof(HeadlineState));
        OnPropertyChanged(nameof(IsRunning));
    }

    private void OpenUrl(string url)
    {
        try
        {
            // Launch through Explorer so the browser runs as the normal user, not elevated like this app.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
            Log.Add(LogSeverity.Info, LogCategory.Device, $"Opened {url} in the browser.");
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't open the browser for {url}: {ex.Message}", ex.ToString());
        }
    }

    public void ShowError(string message, string? detail = null)
    {
        ErrorMessage = message;
        ErrorDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    private void SaveSettings()
    {
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) { Log.Add(LogSeverity.Warning, LogCategory.App, $"Couldn't save settings: {ex.Message}", ex.ToString()); }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        if (_prober is { } p) await p.DisposeAsync();
        await _monitor.DisposeAsync();
    }
}

public sealed record LogLevelOption(LogLevelFilter Level, string Label)
{
    public override string ToString() => Label;
}

public sealed record LogAreaOption(LogCategory? Category, string Label)
{
    public override string ToString() => Label;
}
