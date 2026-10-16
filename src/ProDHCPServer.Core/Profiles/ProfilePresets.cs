namespace ProDHCPServer.Core.Profiles;

/// <summary>Read-only profiles shipped with the app. Duplicate one to change it.</summary>
public static class ProfilePresets
{
    public const string DefaultName = "Default";

    public static Profile Default { get; } = new()
    {
        Name = DefaultName,
        Notes = "A private subnet that is rarely used on venue networks. Good for working on one device or a small rack.",
        IsPreset = true,
    };

    public static IReadOnlyList<Profile> All { get; } =
    [
        Default,
        new()
        {
            Name = "Extron factory subnet",
            Notes = "Extron products ship with the static address 192.168.254.254 (mask 255.255.0.0). This puts your laptop on " +
                    "192.168.254.1 so you can reach a factory-reset unit at 192.168.254.254 while still handing addresses to DHCP devices.",
            ServerIp = "192.168.254.1",
            PoolStart = "192.168.254.100",
            PoolEnd = "192.168.254.199",
            IsPreset = true,
        },
        new()
        {
            Name = "Cisco IP phones (option 150)",
            Notes = "Cisco IP phones look for their TFTP/call manager server in option 150. Duplicate this profile and change " +
                    "option 150 to your server's address.",
            TftpServers = "192.168.100.10",
            IsPreset = true,
        },
        new()
        {
            Name = "Poly phones (option 66)",
            Notes = "Poly (Polycom) phones look for their provisioning server in option 66. Duplicate this profile and change " +
                    "option 66 to your provisioning server's address or URL.",
            TftpServerName = "http://192.168.100.10/",
            IsPreset = true,
        },
        new()
        {
            Name = "Bench test (2-minute leases)",
            Notes = "Short leases, so you can watch a device renew within a few minutes. Uses 10.10.10.x.",
            ServerIp = "10.10.10.1",
            PoolStart = "10.10.10.100",
            PoolEnd = "10.10.10.199",
            LeaseMinutes = 2,
            IsPreset = true,
        },
    ];
}
