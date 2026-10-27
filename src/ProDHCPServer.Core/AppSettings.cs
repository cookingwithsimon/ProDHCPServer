using System.Text.Json;

namespace ProDHCPServer.Core;

/// <summary>
/// User preferences saved between runs. The server fields are from Phase 1, before profiles: they are only
/// read once, to turn them into a "My settings" profile (see <see cref="Profiles.ProfileLibrary.Load"/>).
/// </summary>
public sealed record AppSettings
{
    public string? LastAdapterId { get; init; }
    /// <summary>Name of the profile Start uses.</summary>
    public string? ActiveProfile { get; init; }
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
    /// <summary>Which look the window uses (a name from the App's theme list); unknown values fall back to the default.</summary>
    public string Look { get; init; } = "Faceplate";
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
