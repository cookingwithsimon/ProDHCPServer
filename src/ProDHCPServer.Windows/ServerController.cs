using System.Net;
using System.Runtime.Versioning;
using ProDHCPServer.Core;
using ProDHCPServer.Core.Adapters;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

/// <summary>
/// Runs one serving session: back up and set the adapter, check for other DHCP servers, then keep
/// UDP 67 open while the link and static IP are present. Re-checks for other servers every time
/// the link comes back, because the cable may have been moved to a different network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerController : IAsyncDisposable
{
    private readonly IAdapterControl _adapters;
    private readonly AdapterSessionManager _sessions;
    private readonly SessionLog _log;
    private readonly string _appVersion;
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private DhcpSocketServer? _socket;
    private volatile bool _overrideOtherServer;

    public ServerController(IAdapterControl adapters, AdapterSessionManager sessions, SessionLog log, string appVersion)
    {
        _adapters = adapters;
        _sessions = sessions;
        _log = log;
        _appVersion = appVersion;
    }

    public bool IsActive { get; private set; }
    public string? AdapterId { get; private set; }
    public DhcpServerConfig? Config { get; private set; }
    public DhcpServerEngine? Engine { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public bool Listening => _socket?.IsListening == true;
    public DateTimeOffset? ListeningSince { get; private set; }
    public bool CheckingForOtherServers { get; private set; }
    public ForeignDhcpServer? OtherServer { get; private set; }
    public bool OtherServerOverridden => _overrideOtherServer;
    public string? ServerError { get; private set; }
    public string? ServerErrorDetail { get; private set; }
    public DateTimeOffset? LinkUpSince { get; private set; }
    public TimeSpan? ReceiveStalledFor => _receiveWatch.StalledFor;
    private readonly ReceiveWatch _receiveWatch = new();

    /// <summary>Raised whenever something the status panel shows has changed. Worker thread.</summary>
    public event Action? Changed;
    public event Action<DhcpEngineEvent>? DhcpEvent;

    /// <summary>Starts serving. Throws <see cref="AdapterOperationException"/> with a plain-English reason if it can't.</summary>
    public void Start(AdapterInfo adapter, DhcpServerConfig config)
    {
        if (IsActive) throw new InvalidOperationException("Already running.");
        var problems = config.Validate();
        if (problems.Count > 0) throw new AdapterOperationException(string.Join(" ", problems));
        if (adapter.Kind == AdapterKind.Vpn) throw new AdapterOperationException($"\"{adapter.Name}\" is a VPN adapter. Pick the Ethernet port the device is plugged into.");

        ServerError = null;
        ServerErrorDetail = null;
        OtherServer = null;
        _receiveWatch.Reset();
        _overrideOtherServer = false;

        // Passive check before touching anything: did Windows just get an address from a DHCP server on this port?
        if (adapter is { MediaConnected: true, DhcpEnabled: true, DhcpServer: { } dhcpServer } && adapter.Addresses.Any(a => !IPv4.IsApipa(a.Address)))
        {
            var current = adapter.Addresses.First(a => !IPv4.IsApipa(a.Address)).Address;
            OtherServer = new ForeignDhcpServer(dhcpServer, null, current, adapter.Gateways.FirstOrDefault(),
                $"Windows got {current} on \"{adapter.Name}\" from the DHCP server at {dhcpServer}.");
            _log.Add(LogSeverity.Error, LogCategory.Server, $"Another DHCP server ({dhcpServer}) already gave this laptop {current} on \"{adapter.Name}\". Not serving.");
        }

        try { Firewall.Allow(); _log.Add(LogSeverity.Info, LogCategory.App, "Windows Firewall rule added for UDP 67/68."); }
        catch (AdapterOperationException ex) { _log.Add(LogSeverity.Warning, LogCategory.App, $"{ex.Message} Devices may not be able to reach the server.", ex.Detail); }

        var record = _sessions.Begin(adapter.Id, config.ServerIp, config.PrefixLength, _appVersion);
        _log.Add(LogSeverity.Info, LogCategory.Adapter, $"Backed up \"{adapter.Name}\" ({record.Snapshot.Describe()}).");
        _log.Add(LogSeverity.Info, LogCategory.Adapter, $"Set \"{adapter.Name}\" to {config.ServerIp}/{config.PrefixLength}.");

        var engine = new DhcpServerEngine(config);
        engine.EventRaised += OnEngineEvent;
        Engine = engine;
        Config = config;
        AdapterId = adapter.Id;
        StartedAt = DateTimeOffset.Now;
        IsActive = true;
        _cts = new CancellationTokenSource();
        _supervisor = Task.Run(() => Supervise(adapter, engine, _cts.Token));
        Changed?.Invoke();
    }

    /// <summary>The user confirmed they want to serve even though another DHCP server was seen.</summary>
    public void OverrideOtherServer()
    {
        if (OtherServer is null) return;
        _overrideOtherServer = true;
        _log.Add(LogSeverity.Warning, LogCategory.Server, $"Serving anyway, although another DHCP server ({OtherServer.ServerId}) is on this link.");
        Changed?.Invoke();
    }

    private async Task Supervise(AdapterInfo initial, DhcpServerEngine engine, CancellationToken ct)
    {
        bool linkWasUp = initial.MediaConnected;
        bool probedThisLink = false;
        if (linkWasUp) LinkUpSince = DateTimeOffset.Now;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var adapter = _adapters.GetAdapter(initial.Id);
                bool linkUp = adapter is { MediaConnected: true };
                bool ipReady = adapter?.HasAddress(engine.Config.ServerIp) == true;

                if (linkUp && !linkWasUp)
                {
                    LinkUpSince = DateTimeOffset.Now;
                    probedThisLink = false;
                    // The cable may now be in a different network: forget the old result and check again.
                    if (!_overrideOtherServer) OtherServer = null;
                    _log.Add(LogSeverity.Info, LogCategory.Adapter, $"Link up on \"{initial.Name}\" ({adapter!.SpeedText}, {adapter.DuplexText}).");
                    Changed?.Invoke();
                }
                else if (!linkUp && linkWasUp)
                {
                    LinkUpSince = null;
                    _log.Add(LogSeverity.Warning, LogCategory.Adapter, adapter is null ? $"\"{initial.Name}\" disappeared (USB adapter unplugged?)." : $"Link down on \"{initial.Name}\".");
                    await StopSocket();
                    Changed?.Invoke();
                }
                linkWasUp = linkUp;

                bool wasStalled = _receiveWatch.StalledFor is not null;
                var stalled = _receiveWatch.Update(DateTimeOffset.Now, linkUp, adapter?.BytesReceived, adapter?.BytesSent);
                if (stalled is not null && !wasStalled)
                {
                    var hint = adapter is not null && AdapterRules.KnownDriverProblem(adapter) is { } problem ? $" {problem}" : "";
                    _log.Add(LogSeverity.Error, LogCategory.Adapter,
                        $"\"{initial.Name}\" has received nothing for {(int)stalled.Value.TotalSeconds} s although the link is up and this laptop kept sending.{hint}",
                        adapter?.Driver is { } d ? $"Driver: {d} ({d.HardwareId})" : null);
                    Changed?.Invoke();
                }
                else if (stalled is null && wasStalled && linkUp)
                {
                    _log.Add(LogSeverity.Info, LogCategory.Adapter, $"\"{initial.Name}\" is receiving again.");
                    Changed?.Invoke();
                }

                if (linkUp && ipReady && _socket is null)
                {
                    if (!probedThisLink && !_overrideOtherServer && OtherServer is null)
                    {
                        CheckingForOtherServers = true;
                        Changed?.Invoke();
                        var result = await OtherServerDetector.ProbeAsync(engine.Config.ServerIp, adapter!.Index, adapter.Mac, engine.Config.ServerIp, TimeSpan.FromSeconds(4), ct);
                        CheckingForOtherServers = false;
                        probedThisLink = true;
                        if (result.Limitation is { } limit) _log.Add(LogSeverity.Warning, LogCategory.Server, limit);
                        if (result.Found is { } found)
                        {
                            OtherServer = found;
                            _log.Add(LogSeverity.Error, LogCategory.Server,
                                $"Another DHCP server answered at {found.ServerId} (offering {found.OfferedIp}). Not serving. You may be plugged into a venue network.", found.Detail);
                        }
                        else _log.Add(LogSeverity.Success, LogCategory.Server, "No other DHCP server answered on this link.");
                        Changed?.Invoke();
                    }

                    if (OtherServer is null || _overrideOtherServer)
                        TryStartSocket(engine, adapter!.Index);
                }
                else if (_socket is not null && !ipReady)
                {
                    _log.Add(LogSeverity.Warning, LogCategory.Adapter, $"{engine.Config.ServerIp} disappeared from the adapter; waiting for it to come back.");
                    await StopSocket();
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServerError = $"Unexpected error: {ex.Message}";
                ServerErrorDetail = ex.ToString();
                _log.Add(LogSeverity.Error, LogCategory.Server, ServerError, ServerErrorDetail);
                Changed?.Invoke();
            }

            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void TryStartSocket(DhcpServerEngine engine, int interfaceIndex)
    {
        var socket = new DhcpSocketServer(engine, interfaceIndex);
        socket.Problem += p => _log.Add(LogSeverity.Warning, LogCategory.Dhcp, p.Message, p.Detail);
        try
        {
            socket.Start();
        }
        catch (TransportBindException ex) when (ex.AddressNotReady)
        {
            return; // Try again on the next pass.
        }
        catch (TransportBindException ex)
        {
            if (ServerError != ex.Message)
            {
                ServerError = ex.Message;
                ServerErrorDetail = ex.Detail;
                _log.Add(LogSeverity.Error, LogCategory.Server, ex.Message, ex.Detail);
                Changed?.Invoke();
            }
            return;
        }
        _socket = socket;
        ServerError = null;
        ServerErrorDetail = null;
        ListeningSince = DateTimeOffset.Now;
        _log.Add(LogSeverity.Success, LogCategory.Server, $"DHCP server listening on {engine.Config.ServerIp}:67 (pool {engine.Config.PoolStart} to {engine.Config.PoolEnd}).");
        Changed?.Invoke();
    }

    private async Task StopSocket()
    {
        var s = _socket;
        _socket = null;
        ListeningSince = null;
        if (s is not null) await s.DisposeAsync();
    }

    private void OnEngineEvent(DhcpEngineEvent e)
    {
        var severity = e.Kind switch
        {
            DhcpEventKind.LeaseBound or DhcpEventKind.LeaseRenewed => LogSeverity.Success,
            DhcpEventKind.LeaseDeclined or DhcpEventKind.OtherServerChosen or DhcpEventKind.PoolExhausted => LogSeverity.Error,
            DhcpEventKind.BroadcastFallback or DhcpEventKind.LeaseExpired => LogSeverity.Warning,
            DhcpEventKind.Sent when e.MessageType == DhcpMessageType.Nak => LogSeverity.Warning,
            _ => LogSeverity.Info,
        };
        _log.Add(new LogEntry(e.Time, severity, LogCategory.Dhcp, e.Message, e.Detail));
        DhcpEvent?.Invoke(e);
        Changed?.Invoke();
    }

    /// <summary>Stops serving and puts the adapter back. Safe to call more than once.</summary>
    public async Task<RestoreResult> StopAsync()
    {
        _cts?.Cancel();
        if (_supervisor is not null) { try { await _supervisor; } catch { /* already logged */ } }
        await StopSocket();
        _supervisor = null;
        _cts?.Dispose();
        _cts = null;

        RestoreResult result;
        try { result = _sessions.End(); }
        catch (Exception ex) { result = new RestoreResult(false, [ex.Message], "Restoring the adapter failed."); }

        try { Firewall.Remove(); } catch { /* best effort */ }

        if (IsActive || !result.Success)
            _log.Add(result.Success ? LogSeverity.Success : LogSeverity.Error, LogCategory.Adapter, result.Summary,
                result.Problems.Count > 0 ? string.Join(Environment.NewLine, result.Problems) : null);

        if (Engine is { } engine) engine.EventRaised -= OnEngineEvent;
        IsActive = false;
        CheckingForOtherServers = false;
        ListeningSince = null;
        LinkUpSince = null;
        _receiveWatch.Reset();
        Changed?.Invoke();
        return result;
    }

    /// <summary>Last-ditch synchronous restore for crash handlers and Windows shutdown.</summary>
    public RestoreResult EmergencyRestore()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket?.DisposeAsync().AsTask().Wait(2000); } catch { }
        try { Firewall.Remove(); } catch { }
        try { return _sessions.End(); }
        catch (Exception ex) { return new RestoreResult(false, [ex.Message], "Emergency restore failed."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsActive) await StopAsync();
    }
}
