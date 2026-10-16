using System.Net;
using System.Text.Json;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core.Adapters;

/// <summary>An adapter operation that failed, with a plain-English message and the raw detail.</summary>
public sealed class AdapterOperationException(string message, string? detail = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Detail { get; } = detail;
}

/// <summary>Reads and changes adapter IPv4 settings. The Windows implementation lives in ProDHCPServer.Windows.</summary>
public interface IAdapterControl
{
    IReadOnlyList<AdapterInfo> ListAdapters();
    AdapterInfo? GetAdapter(string adapterId);
    AdapterSnapshot Capture(string adapterId);
    /// <summary>Replace all IPv4 settings with a single static address (no gateway, no DNS).</summary>
    void ApplyStaticAddress(string adapterId, IPAddress address, int prefixLength);
    void AddAddress(string adapterId, IPAddress address, int prefixLength);
    void RemoveAddress(string adapterId, IPAddress address);
    void Restore(AdapterSnapshot snapshot);
    void SetAdapterEnabled(string adapterId, bool enabled);
}

public interface ISessionStore
{
    SessionRecord? Load();
    void Save(SessionRecord record);
    void Clear();
}

/// <summary>Keeps the session record as JSON, written atomically (temp file + rename).</summary>
public sealed class FileSessionStore(string path) : ISessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; } = path;

    public SessionRecord? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(Path), Json);
        }
        catch (JsonException ex)
        {
            throw new AdapterOperationException(
                $"The saved adapter backup at {Path} is damaged and can't be read. Your adapter may need to be set back to its original settings by hand.",
                ex.ToString(), ex);
        }
    }

    public void Save(SessionRecord record)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, Path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public sealed record RestoreResult(bool Success, IReadOnlyList<string> Problems, string Summary);

/// <summary>
/// Owns the "back up, change, always put back" lifecycle for the serving adapter.
/// The backup is saved before the first change, so every exit path (Stop, close, crash
/// handler, next launch) can restore it.
/// </summary>
public sealed class AdapterSessionManager(IAdapterControl adapters, ISessionStore store, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly object _gate = new();
    private SessionRecord? _active;

    public SessionRecord? Active
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>A backup left behind by a session that did not shut down cleanly.</summary>
    public SessionRecord? FindPendingRecovery()
    {
        lock (_gate) return _active is null ? store.Load() : null;
    }

    /// <summary>Backs up the adapter, saves the backup, then applies the server's static address.</summary>
    public SessionRecord Begin(string adapterId, IPAddress serverIp, int prefixLength, string appVersion = "")
    {
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("A session is already running. Stop it before starting another.");
            if (store.Load() is { } leftover)
                throw new AdapterOperationException(
                    $"There is an unrestored backup of \"{leftover.Snapshot.AdapterName}\" from {leftover.StartedAt:g}. Restore it before starting, so the original settings aren't lost.");

            var snapshot = adapters.Capture(adapterId);
            var record = new SessionRecord
            {
                Snapshot = snapshot,
                ServerIp = serverIp,
                PrefixLength = prefixLength,
                StartedAt = _now(),
                ProcessId = Environment.ProcessId,
                AppVersion = appVersion,
            };
            store.Save(record);
            _active = record;

            try
            {
                adapters.ApplyStaticAddress(adapterId, serverIp, prefixLength);
            }
            catch (Exception ex)
            {
                var restore = RestoreLocked(record);
                var message = $"Couldn't set {serverIp}/{prefixLength} on \"{snapshot.AdapterName}\": {Plain(ex)}";
                message += restore.Success ? " The original settings were put back." : $" Putting the original settings back also failed: {string.Join(" ", restore.Problems)}";
                throw new AdapterOperationException(message, Detail(ex), ex);
            }
            return record;
        }
    }

    /// <summary>Disables another adapter (e.g. Wi-Fi) for the session; it is re-enabled on restore.</summary>
    public void DisableForSession(AdapterInfo other)
    {
        lock (_gate)
        {
            var active = _active ?? throw new InvalidOperationException("No session is running.");
            if (active.DisabledAdapters.Any(d => d.AdapterId == other.Id)) return;
            _active = active with { DisabledAdapters = [.. active.DisabledAdapters, new DisabledAdapter(other.Id, other.Name)] };
            store.Save(_active);
            adapters.SetAdapterEnabled(other.Id, false);
        }
    }

    /// <summary>Adds a temporary secondary address; removed again on restore.</summary>
    public void AddTemporaryAddress(IPAddress address, int prefixLength)
    {
        lock (_gate)
        {
            var active = _active ?? throw new InvalidOperationException("No session is running.");
            var assignment = new AddressAssignment(address, prefixLength);
            _active = active with { TemporaryAddresses = [.. active.TemporaryAddresses, assignment] };
            store.Save(_active);
            adapters.AddAddress(active.Snapshot.AdapterId, address, prefixLength);
        }
    }

    /// <summary>Removes a temporary address before Stop. It stays in the backup until Windows has removed it.</summary>
    public void RemoveTemporaryAddress(IPAddress address)
    {
        lock (_gate)
        {
            var active = _active ?? throw new InvalidOperationException("No session is running.");
            adapters.RemoveAddress(active.Snapshot.AdapterId, address);
            _active = active with { TemporaryAddresses = active.TemporaryAddresses.Where(t => !t.Address.Equals(address)).ToList() };
            store.Save(_active);
        }
    }

    /// <summary>Puts the adapter back and verifies it. The backup is only deleted once that succeeds.</summary>
    public RestoreResult End()
    {
        lock (_gate)
        {
            var record = _active ?? store.Load();
            if (record is null) return new RestoreResult(true, [], "Nothing to restore.");
            return RestoreLocked(record);
        }
    }

    /// <summary>Restores a backup found at launch.</summary>
    public RestoreResult Recover(SessionRecord record)
    {
        lock (_gate) return RestoreLocked(record);
    }

    /// <summary>Forgets a leftover backup without applying it (the user chose to keep current settings).</summary>
    public void DiscardPendingRecovery()
    {
        lock (_gate)
        {
            if (_active is null) store.Clear();
        }
    }

    private RestoreResult RestoreLocked(SessionRecord record)
    {
        var problems = new List<string>();
        var snapshot = record.Snapshot;

        foreach (var temp in record.TemporaryAddresses)
        {
            try { adapters.RemoveAddress(snapshot.AdapterId, temp.Address); }
            catch (Exception ex) { problems.Add($"Couldn't remove temporary address {temp}: {Plain(ex)}"); }
        }

        bool adapterPresent = adapters.GetAdapter(snapshot.AdapterId) is not null;
        if (!adapterPresent)
        {
            problems.Add($"\"{snapshot.AdapterName}\" isn't present right now (USB adapter unplugged?). Plug it back in and restore again.");
        }
        else
        {
            try
            {
                adapters.Restore(snapshot);
                var after = adapters.Capture(snapshot.AdapterId);
                problems.AddRange(SnapshotDifferences(snapshot, after));
            }
            catch (Exception ex)
            {
                problems.Add($"Couldn't restore \"{snapshot.AdapterName}\": {Plain(ex)}");
            }
        }

        foreach (var disabled in record.DisabledAdapters)
        {
            try { adapters.SetAdapterEnabled(disabled.AdapterId, true); }
            catch (Exception ex) { problems.Add($"Couldn't turn \"{disabled.Name}\" back on: {Plain(ex)}"); }
        }

        if (problems.Count == 0)
        {
            store.Clear();
            _active = null;
            return new RestoreResult(true, [], $"\"{snapshot.AdapterName}\" is back to: {snapshot.Describe()}.");
        }
        // Keep the backup on disk so the restore can be retried.
        _active = null;
        return new RestoreResult(false, problems, $"\"{snapshot.AdapterName}\" may not be fully restored. The backup is kept so you can retry.");
    }

    /// <summary>Plain-English differences between the expected and actual settings. Empty means they match.</summary>
    public static IReadOnlyList<string> SnapshotDifferences(AdapterSnapshot expected, AdapterSnapshot actual)
    {
        var d = new List<string>();
        if (expected.DhcpEnabled != actual.DhcpEnabled)
            d.Add(expected.DhcpEnabled ? "The adapter is still on a static IP instead of automatic (DHCP)." : "The adapter is on automatic (DHCP) instead of its static IP.");
        if (!expected.DhcpEnabled)
        {
            var missing = expected.StaticAddresses.Where(e => !actual.StaticAddresses.Contains(e)).ToList();
            var extra = actual.StaticAddresses.Where(a => !expected.StaticAddresses.Contains(a)).ToList();
            if (missing.Count > 0) d.Add($"Missing address(es): {string.Join(", ", missing)}.");
            if (extra.Count > 0) d.Add($"Unexpected address(es) still present: {string.Join(", ", extra)}.");
            if (!SameSet(expected.Gateways, actual.Gateways))
                d.Add($"Gateway is {Fmt(actual.Gateways)} instead of {Fmt(expected.Gateways)}.");
        }
        if (expected.DnsFromDhcp != actual.DnsFromDhcp)
            d.Add(expected.DnsFromDhcp ? "DNS is still set by hand instead of automatic." : "DNS is automatic instead of the servers it had.");
        else if (!expected.DnsFromDhcp && !expected.StaticDnsServers.SequenceEqual(actual.StaticDnsServers))
            d.Add($"DNS servers are {Fmt(actual.StaticDnsServers)} instead of {Fmt(expected.StaticDnsServers)}.");
        return d;
    }

    private static bool SameSet(IReadOnlyList<IPAddress> a, IReadOnlyList<IPAddress> b) =>
        a.Select(IPv4.ToUInt32).Order().SequenceEqual(b.Select(IPv4.ToUInt32).Order());

    private static string Fmt(IReadOnlyList<IPAddress> list) => list.Count == 0 ? "none" : string.Join(", ", list);

    private static string Plain(Exception ex) => ex.Message;

    private static string Detail(Exception ex) => ex is AdapterOperationException { Detail: { } d } ? d : ex.ToString();
}
