namespace ProDHCPServer.Core.Profiles;

/// <summary>
/// Read-only profiles shipped with the app. Duplicate one to change it.
/// Every factory address here is from the manufacturer's own documentation, linked in <see cref="Profile.Sources"/>
/// (checked October 2026). The pools are chosen so they never hand out a documented factory address.
/// </summary>
public static class ProfilePresets
{
    public const string DefaultName = "Default";

    public static Profile Default { get; } = new()
    {
        Name = DefaultName,
        Notes = "A private subnet that is rarely used on venue networks. Most networked AV gear asks for an address by DHCP " +
                "out of the box and takes one from this: Q-SYS, Biamp Tesira and Parlé, Shure MXA, Sennheiser TeamConnect Ceiling 2, " +
                "Dante devices, Symetrix, Crestron 4-Series, touch panels and DM NVX, AMX NX, Kramer, Atlona and Extron NAV.",
        Sources =
        [
            new("Q-SYS: default IP address of Cores and peripherals", "https://support.qsys.com/en_US/faq/faq-%7C-what-is-the-default-ip-address-for-q-sys-cores-and-peripherals"),
            new("Biamp: Tesira factory defaults (includes Parlé)", "https://support.biamp.com/Tesira/Miscellaneous/How_to_reset_a_Tesira_device_to_factory_defaults"),
            new("Shure: MXA910 user guide", "https://pubs.shure.com/view/guide/MXA910/en-US.pdf"),
            new("Sennheiser: TCC 2 / TCC M network troubleshooting", "https://help.sennheiser.com/hc/en-us/articles/39123738203161-TCC-2-TCC-M-won-t-appear-in-Control-Cockpit-Troubleshooting"),
            new("Audinate: Dante information for network administrators", "https://www.getdante.com/resources/catalog/networking/dante-information-for-network-administrators/"),
            new("Symetrix: connecting to Symetrix devices", "https://www.symetrix.co/tech-tips/connecting-to-symetrix-devices-with-a-pc"),
            new("Crestron: CP4/CP4N quick start", "https://www.crestron.com/getmedia/cb15afc5-9dfa-412f-9ac2-439a5a312854/mg_qs_cp4-cp4n"),
            new("Crestron: DM NVX quick start", "https://www.crestron.com/getmedia/76b2a92b-b8c8-44f7-8782-79b9a760a188/mg_qs_dm-nvx-363-360"),
            new("AMX: NX-1200 quick start", "https://adn.harmanpro.com/site_elements/resources/2141_1522083687/NX-1200.QuickStartGuide_original.pdf"),
            new("Kramer: SL-240C user manual", "https://cdn.kramerav.com/web/downloads/manuals/kramer_sl-240c_user_manual.pdf"),
            new("Atlona: KB01412, web interface with DHCP", "https://support.atlona.com/hc/en-us/articles/360001487674-KB01412-How-to-access-the-web-interface-of-an-Atlona-system-with-DHCP-enabled"),
            new("Extron: NAV E 222 user guide", "https://media.extron.com/public/download/files/userman/68-3825-01_A_NAV-E-222_User_Guide.pdf"),
        ],
        IsPreset = true,
    };

    public static IReadOnlyList<Profile> All { get; } =
    [
        Default,
        new()
        {
            Name = "192.168.254.x: Extron",
            Notes = "Extron switchers and scalers ship on 192.168.254.254 (mask 255.255.0.0), and IPCP Pro control processors on " +
                    "192.168.254.250 (mask 255.255.255.0) with DHCP off. This puts your laptop on 192.168.254.1 so you can reach a " +
                    "factory-reset unit, while still handing out .100 to .199 to anything that asks.",
            ServerIp = "192.168.254.1",
            PoolStart = "192.168.254.100",
            PoolEnd = "192.168.254.199",
            Sources =
            [
                new("Extron: IN1606 setup guide", "https://media.extron.com/public/download/files/userman/68-2290-50_B_IN1606_SUG.pdf"),
                new("Extron: IPCP Pro user guide", "https://media.extron.com/public/download/files/userman/68-2438-01_M_IPCP_Pro_UG.pdf"),
            ],
            IsPreset = true,
        },
        new()
        {
            Name = "192.168.0.x: Lightware, Christie, Sharp/NEC, Bose",
            Notes = "Factory addresses on 192.168.0.x: Lightware (for example UMX-HDMI-140) 192.168.0.100, Christie GS Series projectors " +
                    "192.168.0.100, Sharp/NEC MultiSync M displays 192.168.0.10, Bose ESP 192.168.0.160, PowerMatch 192.168.0.40 (4-channel) " +
                    "or .80 (8-channel), CC-64 192.168.0.176. This puts your laptop on 192.168.0.1 and only hands out .200 to .230, " +
                    "clear of all of those. Only one device with a given address can be on the cable at a time.",
            ServerIp = "192.168.0.1",
            PoolStart = "192.168.0.200",
            PoolEnd = "192.168.0.230",
            Sources =
            [
                new("Lightware: UMX-HDMI-140 quick start", "https://assets.prod.pim.lightware.com/assets/File-Downloads/Guides-and-Manuals/Quick-Start-Guide/UMX-HDMI-140_series_QSG.pdf"),
                new("Christie: GS Series 1075 user manual", "https://www.christiedigital.com/globalassets/resources/public/020-001225-04-christie-lit-man-usr-gs-series-1075.pdf"),
                new("Sharp/NEC: MultiSync M installation guide", "https://assets.sharpnecdisplays.us/documents/installationguides/m-series-installationguide.pdf"),
                new("Bose Professional: IP addresses of hardware", "https://boseprofessional.com/support/article/ip-address-hardware"),
            ],
            IsPreset = true,
        },
        new()
        {
            Name = "192.168.1.x: Kramer, Atlona, ClickShare, AMX",
            Notes = "Addresses on 192.168.1.x: Kramer (for example SL-240C) falls back to 192.168.1.39 (mask 255.255.0.0) when no DHCP server answers, " +
                    "some Atlona products fall back to 192.168.1.150, a Barco ClickShare base unit is reached at 192.168.1.23 over a " +
                    "direct cable, and an AMX NX controller switched to static uses 192.168.1.3. This puts your laptop on 192.168.1.1 " +
                    "and only hands out .200 to .249.",
            ServerIp = "192.168.1.1",
            PoolStart = "192.168.1.200",
            PoolEnd = "192.168.1.249",
            Sources =
            [
                new("Kramer: SL-240C user manual", "https://cdn.kramerav.com/web/downloads/manuals/kramer_sl-240c_user_manual.pdf"),
                new("Atlona: KB01412, web interface with DHCP", "https://support.atlona.com/hc/en-us/articles/360001487674-KB01412-How-to-access-the-web-interface-of-an-Atlona-system-with-DHCP-enabled"),
                new("Barco: ClickShare Configurator connections", "https://www.barco.com/en/support/knowledge-base/2324-clickshare-configurator-connections"),
                new("AMX: NX-1200 quick start", "https://adn.harmanpro.com/site_elements/resources/2141_1522083687/NX-1200.QuickStartGuide_original.pdf"),
            ],
            IsPreset = true,
        },
        new()
        {
            Name = "Cisco IP phones (option 150)",
            Notes = "Cisco IP phones find their TFTP/call manager server through DHCP option 150 (Cisco says option 66 works but " +
                    "isn't recommended). Duplicate this profile and change option 150 to your server's address.",
            TftpServers = "192.168.100.10",
            Sources =
            [
                new("Cisco: Unified CM TFTP configuration", "https://www.cisco.com/c/en/us/td/docs/voice_ip_comm/cucm/admin/12_5_1SU1/systemConfig/cucm_b_system-configuration-guide-1251su1/cucm_b_system-configuration-guide-1251su1_restructured_chapter_0101101.html"),
            ],
            IsPreset = true,
        },
        new()
        {
            Name = "Poly phones (option 66)",
            Notes = "Poly (Polycom) phones find their provisioning server through DHCP option 66. Duplicate this profile and change " +
                    "option 66 to your provisioning server's address or URL. Poly Lens can also use option 160 or 161.",
            TftpServerName = "http://192.168.100.10/",
            Sources =
            [
                new("Poly: UC Software provisioning guide", "https://kaas.hpcloud.hp.com/pdf-public/pdf_9122416_en-US-1.pdf"),
                new("Poly Lens: provisioning phones", "https://info.lens.poly.com/docs/onboard/provisioning-phones"),
            ],
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
