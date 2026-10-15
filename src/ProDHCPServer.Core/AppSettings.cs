using System.Text.Json;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Core;

/// <summary>User preferences saved between runs. Phase 2 replaces the server fields with profiles.</summary>
public sealed record AppSettings
{
    public string? LastAdapterId { get; init; }
    public string ServerIp { get; init; } = "192.168.100.1";
    public int PrefixLength { get; init; } = 24;
    public string PoolStart { get; init; } = "192.168.100.100";
    public string PoolEnd { get; init; } = "192.168.100.199";
    public int LeaseMinutes { get; init; } = 60;
    public string? Router { get; init; }
    public string? DnsServers { get; init; }
    public bool AlwaysBroadcast { get; init; }
    /// <summary>"System", "Dark" or "Light".</summary>
    public string Theme { get; init; } = "System";

    /// <summary>Builds the server config, or returns plain-English problems.</summary>
    public (DhcpServerConfig? Config, IReadOnlyList<string> Errors) ToConfig()
    {
        var errors = new List<string>();
        if (!IPv4.TryParse(ServerIp, out var server)) errors.Add($"Server IP \"{ServerIp}\" isn't a valid IPv4 address.");
        if (!IPv4.TryParse(PoolStart, out var start)) errors.Add($"Pool start \"{PoolStart}\" isn't a valid IPv4 address.");
        if (!IPv4.TryParse(PoolEnd, out var end)) errors.Add($"Pool end \"{PoolEnd}\" isn't a valid IPv4 address.");
        System.Net.IPAddress? router = null;
        if (!string.IsNullOrWhiteSpace(Router))
        {
            if (IPv4.TryParse(Router, out var r)) router = r;
            else errors.Add($"Gateway \"{Router}\" isn't a valid IPv4 address.");
        }
        var dns = new List<System.Net.IPAddress>();
        foreach (var part in (DnsServers ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPv4.TryParse(part, out var d)) dns.Add(d);
            else errors.Add($"DNS server \"{part}\" isn't a valid IPv4 address.");
        }
        if (errors.Count > 0) return (null, errors);

        var config = new DhcpServerConfig
        {
            ServerIp = server,
            PrefixLength = PrefixLength,
            PoolStart = start,
            PoolEnd = end,
            LeaseTime = TimeSpan.FromMinutes(LeaseMinutes),
            Router = router,
            DnsServers = dns,
            AlwaysBroadcast = AlwaysBroadcast,
        };
        var problems = config.Validate();
        return problems.Count > 0 ? (null, problems) : (config, []);
    }
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged settings file should never stop the app from starting.
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
