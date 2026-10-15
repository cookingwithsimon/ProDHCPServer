using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
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
    private MacAddress? _userFocus;

    public MainViewModel(IAdapterControl adapters, AdapterSessionManager sessions, ServerController controller,
        SessionLog log, SettingsStore settingsStore, Dispatcher dispatcher)
    {
        _adapters = adapters;
        _sessions = sessions;
        _controller = controller;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        Log = log;
        _settings = settingsStore.Load();
        Settings = new SettingsViewModel(_settings);
        Steps = new ObservableCollection<StepViewModel>(Enum.GetValues<StepId>().Select(id => new StepViewModel(id)));

        foreach (var entry in log.Snapshot().TakeLast(MaxLogRows)) LogRows.Add(new LogItemViewModel(entry));
        if (LogRows.LastOrDefault() is { } last) LatestLogText = $"{last.Entry.Time:HH:mm:ss}  {last.Message}";
        log.Added += entry => _dispatcher.BeginInvoke(() =>
        {
            LogRows.Add(new LogItemViewModel(entry));
            LatestLogText = $"{entry.Time:HH:mm:ss}  {entry.Message}";
            while (LogRows.Count > MaxLogRows) LogRows.RemoveAt(0);
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private AdapterItemViewModel? _selectedAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(SettingsEditable))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private bool _isRunning;

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

    public string StartStopText => IsBusy ? (IsRunning ? "Stopping…" : "Starting…") : IsRunning ? "Stop" : "Start";
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
        var (config, _) = _settings.ToConfig();
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
            Ping = _prober?.LastPing,
            ConsecutivePingFailures = _prober?.ConsecutivePingFailures ?? 0,
            WebUi = _prober?.LastWebUi,
            ConflictingAdapters = conflicts,
            ConnectedVpns = vpns,
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
            HeadlineAction = $"Press Start to set {config.ServerIp}/{config.PrefixLength} and hand out {config.PoolStart} to {config.PoolEnd}.";
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
        var config = Settings.TryBuild(_settings);
        if (config is null)
        {
            SettingsOpen = true;
            ShowError("The server settings need fixing before starting.", Settings.Errors);
            return;
        }
        _settings = Settings.ApplyTo(_settings);
        SaveSettings();

        lock (_engineEvents) _engineEvents.Clear();
        Devices.Clear();
        _userFocus = null;
        IsBusy = true;
        try
        {
            await Task.Run(() => _controller.Start(adapter, config));
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
        var answer = MessageBox.Show(
            "Another DHCP server is already on this network. Serving as well can hand out wrong addresses to other people's equipment.\n\nOnly continue if you are sure you are connected just to your own devices.\n\nServe anyway?",
            "Serve DHCP anyway?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _controller.OverrideOtherServer();
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
    private void SaveSettingsForm()
    {
        if (Settings.TryBuild(_settings) is null) return;
        var themeChanged = Settings.Theme != _settings.Theme;
        _settings = Settings.ApplyTo(_settings);
        SaveSettings();
        if (themeChanged) ApplyTheme();
        SettingsOpen = false;
        UpdateStatus();
    }

    [RelayCommand]
    private void SuggestPool() => Settings.SuggestPool();

    [RelayCommand]
    private void ResetSettings()
    {
        Settings.Load(new AppSettings { LastAdapterId = _settings.LastAdapterId, Theme = _settings.Theme });
        Settings.Errors = "";
    }

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
        Theme.Apply(_settings.Theme);
        foreach (var s in Steps) s.Repaint();
        OnPropertyChanged(nameof(HeadlineState));
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
    }
}
