using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Profiles;
using static ProDHCPServer.Core.Tests.Packets;

namespace ProDHCPServer.Core.Tests;

public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdhcp-profiles-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "profiles.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Every_preset_is_valid()
    {
        foreach (var preset in ProfilePresets.All)
        {
            var (config, errors) = preset.ToConfig();
            Assert.True(errors.Count == 0, $"{preset.Name}: {string.Join(" ", errors)}");
            Assert.NotNull(config);
            Assert.True(preset.IsPreset);
            Assert.False(string.IsNullOrWhiteSpace(preset.Notes));
        }
        Assert.Equal(ProfilePresets.All.Count, ProfilePresets.All.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Theory]
    // Factory addresses from the manufacturers' documentation (see the preset sources).
    [InlineData("192.168.254.x: Extron", new[] { "192.168.254.254", "192.168.254.250" })]
    [InlineData("192.168.0.x: Lightware, Christie, Sharp/NEC, Bose", new[] { "192.168.0.100", "192.168.0.10", "192.168.0.160", "192.168.0.40", "192.168.0.80", "192.168.0.176" })]
    [InlineData("192.168.1.x: Kramer, Atlona, ClickShare, AMX", new[] { "192.168.1.39", "192.168.1.150", "192.168.1.23", "192.168.1.3" })]
    public void Brand_presets_reach_factory_addresses_without_handing_them_out(string name, string[] factory)
    {
        var preset = ProfilePresets.All.Single(p => p.Name == name);
        var config = preset.ToConfig().Config!;
        foreach (var ip in factory.Select(IPv4.Parse))
        {
            Assert.True(config.InSubnet(ip), $"{ip} should be reachable from {preset.Summary}");
            Assert.False(config.InPool(ip), $"{ip} must not be handed out by {preset.Name}");
            Assert.NotEqual(ip, config.ServerIp);
        }
    }

    [Fact]
    public void Brand_presets_cite_the_manufacturer()
    {
        foreach (var preset in ProfilePresets.All.Where(p => p.Name != "Bench test (2-minute leases)"))
        {
            Assert.NotEmpty(preset.Sources);
            Assert.All(preset.Sources, s => Assert.StartsWith("https://", s.Url));
            Assert.All(preset.Sources, s => Assert.True(Uri.IsWellFormedUriString(s.Url, UriKind.Absolute), s.Url));
        }
    }

    [Fact]
    public void Extra_options_reach_the_offer()
    {
        var profile = new Profile
        {
            Name = "Phones",
            DomainName = "av.local",
            NtpServers = "192.168.100.1",
            TftpServerName = "http://192.168.100.10/",
            TftpServers = "192.168.100.10, 192.168.100.11",
            VendorSpecificHex = "01 04 C0 A8 64 0A",
            CustomOptions = [new CustomOption(160, CustomOptionFormat.Text, "https://prov.example/"), new CustomOption(242, CustomOptionFormat.Hex, "0x01,0x02")],
        };
        var (config, errors) = profile.ToConfig();
        Assert.Empty(errors);

        var engine = new DhcpServerEngine(config!);
        var offer = engine.Send(Client(DhcpMessageType.Discover, MacA))!.Packet;
        Assert.Equal("av.local", offer.GetOption(DhcpOptionCode.DomainName)!.AsString());
        Assert.Equal([IPv4.Parse("192.168.100.1")], offer.GetOption(DhcpOptionCode.NtpServers)!.AsAddresses());
        Assert.Equal("http://192.168.100.10/", offer.GetOption(DhcpOptionCode.TftpServerName)!.AsString());
        Assert.Equal(2, offer.GetOption(DhcpOptionCode.TftpServerAddress)!.AsAddresses().Count);
        Assert.Equal([1, 4, 0xC0, 0xA8, 0x64, 0x0A], offer.GetOption(DhcpOptionCode.VendorSpecific)!.Data);
        Assert.Equal("https://prov.example/", offer.GetOption(160)!.AsString());
        Assert.Equal([1, 2], offer.GetOption(242)!.Data);
    }

    [Theory]
    [InlineData(53, "is set by the server itself")]
    [InlineData(3, "has its own field (Gateway)")]
    [InlineData(1, "is set from the subnet field")]
    public void Custom_options_cant_replace_managed_ones(byte code, string reason)
    {
        var (_, errors) = new Profile { Name = "x", CustomOptions = [new CustomOption(code, CustomOptionFormat.Hex, "01")] }.ToConfig();
        Assert.Equal($"Option {code} {reason}.", Assert.Single(errors));
    }

    [Fact]
    public void Bad_option_values_are_explained()
    {
        var (_, errors) = new Profile
        {
            Name = "x",
            VendorSpecificHex = "01 0G",
            NtpServers = "ntp.local",
            CustomOptions =
            [
                new CustomOption(200, CustomOptionFormat.Addresses, ""),
                new CustomOption(201, CustomOptionFormat.Text, new string('a', 300)),
                new CustomOption(202, CustomOptionFormat.Hex, "AA"),
                new CustomOption(202, CustomOptionFormat.Hex, "BB"),
            ],
        }.ToConfig();
        Assert.Contains("Option 43 \"01 0G\" isn't valid hex. Use pairs of hex digits, like 01 04 C0 A8 64 0A.", errors);
        Assert.Contains("NTP server \"ntp.local\" isn't a valid IPv4 address.", errors);
        Assert.Contains("Option 200 is empty.", errors);
        Assert.Contains("Option 201 is too long (300 bytes; a DHCP option holds 255 at most).", errors);
        Assert.Contains("Option 202 is listed more than once.", errors);
    }

    [Theory]
    [InlineData("0104C0A8", new byte[] { 1, 4, 0xC0, 0xA8 })]
    [InlineData("01 04 c0 a8", new byte[] { 1, 4, 0xC0, 0xA8 })]
    [InlineData("01:04-C0,A8", new byte[] { 1, 4, 0xC0, 0xA8 })]
    [InlineData("0x01 0x4", new byte[] { 1, 4 })]
    public void Hex_accepts_common_formats(string text, byte[] expected)
    {
        Assert.True(Profile.TryParseHex(text, out var bytes));
        Assert.Equal(expected, bytes);
        Assert.False(Profile.TryParseHex("ABC", out _));
        Assert.Equal("01 04 C0 A8", Profile.ToHex([1, 4, 0xC0, 0xA8]));
    }

    [Fact]
    public void Save_rename_duplicate_delete_and_reload()
    {
        var lib = new ProfileLibrary(FilePath);
        lib.Load();
        var mine = new Profile { Name = " Venue A ", ServerIp = "10.0.0.1", PoolStart = "10.0.0.100", PoolEnd = "10.0.0.150" };
        lib.Save(mine, null);
        Assert.Equal("Venue A", lib.Find("venue a")!.Name);

        lib.Save(lib.Find("Venue A")! with { Name = "Venue B" }, "Venue A");
        Assert.Null(lib.Find("Venue A"));
        var copy = lib.Duplicate(lib.Find("Venue B")!);
        Assert.Equal("Venue B copy", copy.Name);
        Assert.Equal("Venue B copy (2)", lib.Duplicate(lib.Find("Venue B")!).Name);

        var presetCopy = lib.Duplicate(ProfilePresets.Default);
        Assert.Equal("Default copy", presetCopy.Name);
        Assert.False(presetCopy.IsPreset);

        lib.Delete("Venue B copy (2)");
        var reloaded = new ProfileLibrary(FilePath);
        Assert.Null(reloaded.Load());
        Assert.Equal(["Venue B", "Venue B copy", "Default copy"], reloaded.UserProfiles.Select(p => p.Name));
        Assert.Equal("10.0.0.1", reloaded.Find("Venue B")!.ServerIp);
    }

    [Fact]
    public void Presets_are_protected_and_names_unique()
    {
        var lib = new ProfileLibrary(null);
        lib.Save(new Profile { Name = "Mine" }, null);
        Assert.Throws<ProfileException>(() => lib.Save(new Profile { Name = "default" }, null));
        Assert.Throws<ProfileException>(() => lib.Save(ProfilePresets.Default with { Notes = "changed" }, ProfilePresets.DefaultName));
        Assert.Throws<ProfileException>(() => lib.Save(new Profile { Name = "MINE" }, null));
        Assert.Throws<ProfileException>(() => lib.Save(new Profile { Name = "  " }, null));
        Assert.Throws<ProfileException>(() => lib.Delete(ProfilePresets.DefaultName));
        // Saving under its own name is fine.
        lib.Save(new Profile { Name = "Mine", LeaseMinutes = 5 }, "mine");
        Assert.Equal(5, lib.Find("Mine")!.LeaseMinutes);
    }

    [Fact]
    public void Export_then_import_round_trips_and_renames_clashes()
    {
        var source = new ProfileLibrary(null);
        source.Save(new Profile
        {
            Name = "Phones",
            TftpServers = "192.168.100.10",
            CustomOptions = [new CustomOption(160, CustomOptionFormat.Text, "https://prov/")],
        }, null);
        var json = ProfileLibrary.Export(source.UserProfiles);
        Assert.Contains("\"format\": \"ProDHCPServer profiles\"", json);
        Assert.Contains("\"format\": \"text\"", json);
        Assert.DoesNotContain("isPreset", json);

        var target = new ProfileLibrary(null);
        target.Save(new Profile { Name = "Phones" }, null);
        var result = target.Import(json);
        Assert.Empty(result.Problems);
        var imported = Assert.Single(result.Added);
        Assert.Equal("Phones (2)", imported.Name);
        Assert.Equal("https://prov/", imported.CustomOptions[0].Value);
        Assert.Equal("Imported \"Phones (2)\".", result.Describe());
    }

    [Fact]
    public void Import_accepts_a_single_profile_and_skips_broken_ones()
    {
        var lib = new ProfileLibrary(null);
        var single = lib.Import("""{ "name": "Solo", "serverIp": "10.1.1.1", "poolStart": "10.1.1.10", "poolEnd": "10.1.1.20" }""");
        Assert.Equal("Solo", Assert.Single(single.Added).Name);

        var mixed = lib.Import("""
            { "profiles": [
                { "name": "Good", "serverIp": "10.2.2.1", "poolStart": "10.2.2.10", "poolEnd": "10.2.2.20" },
                { "name": "Bad", "serverIp": "10.3.3.1", "poolStart": "10.9.9.10", "poolEnd": "10.9.9.20" },
                { "name": "" }
            ] }
            """);
        Assert.Equal("Good", Assert.Single(mixed.Added).Name);
        Assert.Equal(2, mixed.Problems.Count);
        Assert.StartsWith("Skipped \"Bad\": The pool", mixed.Problems[0]);

        var junk = lib.Import("not json");
        Assert.Empty(junk.Added);
        Assert.StartsWith("This isn't a ProDHCPServer profiles file", Assert.Single(junk.Problems));
    }

    [Fact]
    public void First_run_turns_changed_phase_1_settings_into_a_profile()
    {
        var lib = new ProfileLibrary(FilePath);
        lib.Load(new AppSettings { ServerIp = "10.10.10.1", PoolStart = "10.10.10.100", PoolEnd = "10.10.10.199", AlwaysBroadcast = true });
        var migrated = Assert.Single(lib.UserProfiles);
        Assert.Equal("My settings", migrated.Name);
        Assert.True(migrated.AlwaysBroadcast);
        Assert.True(File.Exists(FilePath));

        // Defaults aren't worth a profile, and an existing file is never overwritten by old settings.
        Assert.Null(ProfileLibrary.FromLegacy(new AppSettings()));
        var again = new ProfileLibrary(FilePath);
        again.Load(new AppSettings { ServerIp = "172.16.0.1" });
        Assert.Equal("10.10.10.1", Assert.Single(again.UserProfiles).ServerIp);
    }

    [Fact]
    public void A_damaged_profiles_file_doesnt_stop_the_app()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ broken");
        var lib = new ProfileLibrary(FilePath);
        Assert.StartsWith("Couldn't read your saved profiles", lib.Load());
        Assert.Equal(ProfilePresets.All.Count, lib.All.Count());
    }
}
