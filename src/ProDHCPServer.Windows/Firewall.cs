using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ProDHCPServer.Windows;

/// <summary>Adds and removes the inbound firewall rules this app needs, scoped to its own exe.</summary>
[SupportedOSPlatform("windows")]
public static class Firewall
{
    public const string RuleName = "ProDHCPServer (DHCP)";
    public const string DiscoveryRuleName = "ProDHCPServer (discovery)";

    /// <summary>
    /// Allows inbound UDP 67 (server) and 68 (other-server probe), plus device discovery (mDNS, UPnP, vendor
    /// protocols and broadcasts from devices on other subnets) on wired adapters, all for this exe only. Idempotent.
    /// </summary>
    public static void Allow()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this program's path.");
        Remove();
        CommandRunner.Netsh("add a Windows Firewall rule for DHCP",
            "advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow",
            $"program={exe}", "protocol=UDP", "localport=67,68", "profile=any", "enable=yes",
            "description=Added by ProDHCPServer while serving DHCP; removed when it stops.");
        CommandRunner.Netsh("add a Windows Firewall rule for finding devices",
            "advfirewall", "firewall", "add", "rule", $"name={DiscoveryRuleName}", "dir=in", "action=allow",
            $"program={exe}", "protocol=any", "interfacetype=lan", "profile=any", "enable=yes",
            "description=Added by ProDHCPServer while it looks for devices on a wired adapter; removed when it stops.");
    }

    public static void Remove()
    {
        // "No rules match" is fine.
        CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={RuleName}"]);
        CommandRunner.Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={DiscoveryRuleName}"]);
    }
}

/// <summary>Finds which process holds a UDP port, to explain "port 67 is in use".</summary>
[SupportedOSPlatform("windows")]
public static partial class PortOwner
{
    [GeneratedRegex(@"^\s*UDP\s+(\S+):(\d+)\s+\S+\s+(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex UdpLine();

    public static string? Describe(int port)
    {
        CommandResult result;
        try { result = CommandRunner.Run("netstat.exe", ["-ano", "-p", "UDP"], TimeSpan.FromSeconds(10)); }
        catch { return null; }

        foreach (Match m in UdpLine().Matches(result.Output))
        {
            if (int.Parse(m.Groups[2].Value) != port) continue;
            int pid = int.Parse(m.Groups[3].Value);
            string name;
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch { name = "an unknown program"; }
            var hint = name.Equals("svchost", StringComparison.OrdinalIgnoreCase)
                ? " This is usually Windows Internet Connection Sharing (Mobile Hotspot or a shared connection). Turn it off and try again."
                : " Close that program (it may be another DHCP server such as Tftpd64 or a vendor tool) and try again.";
            return $"UDP port {port} is already in use by {name} (process {pid}).{hint}";
        }
        return null;
    }
}
