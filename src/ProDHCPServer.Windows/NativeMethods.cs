using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

/// <summary>
/// Static ARP (neighbor) entries, so a unicast OFFER/ACK can reach a client that has no IP yet
/// (RFC 2131 4.1). Uses the IP Helper MIB_IPNET_ROW2 API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class NeighborTable
{
    private const int RowSize = 88;            // sizeof(MIB_IPNET_ROW2) on x64 and arm64
    private const int OffInterfaceIndex = 28;
    private const int OffPhysicalAddress = 40;
    private const int OffPhysicalLength = 72;
    private const int OffState = 76;
    private const int NlnsPermanent = 6;
    private const int ErrorObjectAlreadyExists = 5010;
    private const ushort AfInet = 2;

    private readonly HashSet<(int, uint)> _added = [];

    [LibraryImport("iphlpapi.dll")]
    private static partial int CreateIpNetEntry2(IntPtr row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int SetIpNetEntry2(IntPtr row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int DeleteIpNetEntry2(IntPtr row);

    /// <summary>Adds or updates a neighbor entry. Returns null on success, or the Win32 error text.</summary>
    public string? Set(int interfaceIndex, IPAddress ip, MacAddress mac)
    {
        var row = Build(interfaceIndex, ip, mac);
        try
        {
            int rc = CreateIpNetEntry2(row);
            if (rc == ErrorObjectAlreadyExists) rc = SetIpNetEntry2(row);
            if (rc != 0) return $"{new System.ComponentModel.Win32Exception(rc).Message} (error {rc})";
            lock (_added) _added.Add((interfaceIndex, IPv4.ToUInt32(ip)));
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(row);
        }
    }

    /// <summary>Removes every entry this instance added.</summary>
    public void Clear()
    {
        List<(int, uint)> entries;
        lock (_added) { entries = [.. _added]; _added.Clear(); }
        foreach (var (index, ip) in entries)
        {
            var row = Build(index, IPv4.FromUInt32(ip), MacAddress.Empty);
            try { DeleteIpNetEntry2(row); } finally { Marshal.FreeHGlobal(row); }
        }
    }

    private static IntPtr Build(int interfaceIndex, IPAddress ip, MacAddress mac)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        var zero = new byte[RowSize];
        Marshal.Copy(zero, 0, row, RowSize);
        // SOCKADDR_INET as sockaddr_in: family, port, address.
        Marshal.WriteInt16(row, 0, (short)AfInet);
        Marshal.Copy(ip.GetAddressBytes(), 0, row + 4, 4);
        Marshal.WriteInt32(row, OffInterfaceIndex, interfaceIndex);
        Marshal.Copy(mac.ToBytes(), 0, row + OffPhysicalAddress, 6);
        Marshal.WriteInt32(row, OffPhysicalLength, 6);
        Marshal.WriteInt32(row, OffState, NlnsPermanent);
        return row;
    }
}

[SupportedOSPlatform("windows")]
public static class Elevation
{
    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this exe again with the UAC prompt. Returns false if the user said no.</summary>
    public static bool RelaunchElevated(string args = "")
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this program's path.");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // ERROR_CANCELLED: the user clicked No
        }
    }
}
