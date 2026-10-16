using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProDHCPServer.Core.Profiles;

/// <summary>The outcome of importing a profiles file.</summary>
public sealed record ProfileImportResult(IReadOnlyList<Profile> Added, IReadOnlyList<string> Problems)
{
    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(Added.Count switch
        {
            0 => "No profiles were imported.",
            1 => $"Imported \"{Added[0].Name}\".",
            var n => $"Imported {n} profiles: {string.Join(", ", Added.Select(p => $"\"{p.Name}\""))}.",
        });
        parts.AddRange(Problems);
        return string.Join(" ", parts);
    }
}

/// <summary>
/// The shipped presets plus the user's own profiles, saved as JSON. Profile names are unique,
/// ignoring case. Presets can't be changed or deleted.
/// </summary>
public sealed class ProfileLibrary
{
    public const int MaxImportBytes = 1_000_000;
    public const int MaxProfiles = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string? _path;
    private readonly List<Profile> _user = [];

    /// <param name="path">Where the user's profiles are saved; null keeps them in memory (tests).</param>
    public ProfileLibrary(string? path) => _path = path;

    public IReadOnlyList<Profile> UserProfiles => _user;
    public IEnumerable<Profile> All => ProfilePresets.All.Concat(_user);

    public Profile? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the user's profiles. On first run, if the old single-settings form held anything other
    /// than the defaults, it becomes a profile called "My settings" so nothing typed in Phase 1 is lost.
    /// Returns a problem message when the file couldn't be read (the app still starts).
    /// </summary>
    public string? Load(AppSettings? legacy = null)
    {
        _user.Clear();
        if (_path is null) return null;
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(_path), Json);
                foreach (var p in file?.Profiles ?? []) TryAdd(Clean(p), out _);
                return null;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Couldn't read your saved profiles ({ex.Message}). The presets are still available.";
        }

        if (legacy is not null && FromLegacy(legacy) is { } migrated)
        {
            _user.Add(migrated);
            Save();
        }
        return null;
    }

    internal static Profile? FromLegacy(AppSettings s)
    {
        var p = new Profile
        {
            Name = "My settings",
            Notes = "Your settings from the previous version of ProDHCPServer.",
            ServerIp = s.ServerIp,
            PrefixLength = s.PrefixLength,
            PoolStart = s.PoolStart,
            PoolEnd = s.PoolEnd,
            LeaseMinutes = s.LeaseMinutes,
            Router = s.Router,
            DnsServers = s.DnsServers,
            AlwaysBroadcast = s.AlwaysBroadcast,
        };
        var d = ProfilePresets.Default;
        bool same = p.ServerIp == d.ServerIp && p.PrefixLength == d.PrefixLength && p.PoolStart == d.PoolStart && p.PoolEnd == d.PoolEnd
            && p.LeaseMinutes == d.LeaseMinutes && string.IsNullOrEmpty(p.Router) && string.IsNullOrEmpty(p.DnsServers) && !p.AlwaysBroadcast;
        return same ? null : p;
    }

    /// <summary>Adds or replaces a user profile. <paramref name="originalName"/> is its name before editing (for renames).</summary>
    public void Save(Profile profile, string? originalName)
    {
        profile = Clean(profile);
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ProfileException("Give the profile a name.");
        if (ProfilePresets.All.Any(p => Same(p.Name, profile.Name)))
            throw new ProfileException($"\"{profile.Name}\" is the name of a preset. Pick another name.");
        if (originalName is not null && ProfilePresets.All.Any(p => Same(p.Name, originalName)))
            throw new ProfileException($"\"{originalName}\" is a preset and can't be changed. Duplicate it first.");
        if (_user.Any(p => Same(p.Name, profile.Name) && !Same(p.Name, originalName)))
            throw new ProfileException($"There is already a profile called \"{profile.Name}\". Pick another name.");

        int index = originalName is null ? -1 : _user.FindIndex(p => Same(p.Name, originalName));
        if (index >= 0) _user[index] = profile;
        else
        {
            if (_user.Count >= MaxProfiles) throw new ProfileException($"You already have {MaxProfiles} profiles. Delete some first.");
            _user.Add(profile);
        }
        Save();
    }

    /// <summary>Copies any profile (preset or user) under a new unique name and saves it.</summary>
    public Profile Duplicate(Profile source)
    {
        var copy = Clean(source) with { Name = UniqueName($"{source.Name} copy") };
        Save(copy, null);
        return copy;
    }

    public void Delete(string name)
    {
        if (ProfilePresets.All.Any(p => Same(p.Name, name))) throw new ProfileException($"\"{name}\" is a preset and can't be deleted.");
        _user.RemoveAll(p => Same(p.Name, name));
        Save();
    }

    public string UniqueName(string wanted)
    {
        wanted = wanted.Trim();
        if (Find(wanted) is null) return wanted;
        for (int i = 2; ; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (Find(candidate) is null) return candidate;
        }
    }

    /// <summary>The profiles file format, also used for export.</summary>
    public static string Export(IEnumerable<Profile> profiles) =>
        JsonSerializer.Serialize(new ProfileFile { Profiles = profiles.Select(Clean).ToList() }, Json);

    /// <summary>
    /// Imports profiles from an exported file (or a single profile object). Names that clash get " (2)" added.
    /// Profiles that wouldn't work are skipped with the reason.
    /// </summary>
    public ProfileImportResult Import(string json)
    {
        if (json.Length > MaxImportBytes) return new([], ["The file is too big to be a ProDHCPServer profiles file."]);
        List<Profile> incoming;
        try
        {
            using var doc = JsonDocument.Parse(json);
            incoming = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("profiles", out _)
                ? JsonSerializer.Deserialize<ProfileFile>(json, Json)?.Profiles ?? []
                : [JsonSerializer.Deserialize<Profile>(json, Json)!];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentNullException)
        {
            return new([], [$"This isn't a ProDHCPServer profiles file ({ex.Message.Split('.')[0]})."]);
        }

        var added = new List<Profile>();
        var problems = new List<string>();
        foreach (var raw in incoming.Take(MaxProfiles))
        {
            if (raw is null || string.IsNullOrWhiteSpace(raw.Name)) { problems.Add("Skipped a profile with no name."); continue; }
            var p = Clean(raw);
            var (_, errors) = p.ToConfig();
            if (errors.Count > 0) { problems.Add($"Skipped \"{p.Name}\": {errors[0]}"); continue; }
            p = p with { Name = UniqueName(p.Name) };
            if (!TryAdd(p, out var why)) { problems.Add($"Skipped \"{p.Name}\": {why}"); continue; }
            added.Add(p);
        }
        if (incoming.Count > MaxProfiles) problems.Add($"Only the first {MaxProfiles} profiles were read.");
        if (added.Count > 0) Save();
        return new(added, problems);
    }

    private bool TryAdd(Profile p, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(p.Name)) { why = "it has no name"; return false; }
        if (Find(p.Name) is not null) { why = "the name is already used"; return false; }
        if (_user.Count >= MaxProfiles) { why = $"you already have {MaxProfiles} profiles"; return false; }
        _user.Add(p);
        return true;
    }

    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, Export(_user));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Trims the name and clears the preset flag; nulls from hand-edited JSON become defaults.</summary>
    private static Profile Clean(Profile p) => p with
    {
        Name = (p.Name ?? "").Trim(),
        IsPreset = false,
        CustomOptions = p.CustomOptions ?? [],
        Sources = p.Sources ?? [],
        ServerIp = p.ServerIp ?? "",
        PoolStart = p.PoolStart ?? "",
        PoolEnd = p.PoolEnd ?? "",
    };

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class ProfileFile
    {
        public string Format { get; init; } = "ProDHCPServer profiles";
        public int Version { get; init; } = 1;
        public List<Profile> Profiles { get; init; } = [];
    }
}

public sealed class ProfileException(string message) : Exception(message);
