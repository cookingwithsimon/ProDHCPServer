using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProDHCPServer.Core.Discovery;
using ProDHCPServer.Core.Net;

namespace ProDHCPServer.Windows;

public sealed record ArpEntry(IPAddress Ip, MacAddress Mac, int InterfaceIndex);

/// <summary>Windows' ARP (neighbor) table and one-off ARP requests, from the IP Helper API.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsArp : IArpProber
{
    private const int RowSize = 88;            // sizeof(MIB_IPNET_ROW2) on x64 and arm64
    private const int TableHeader = 8;         // ULONG NumEntries, padded to the row alignment
    private const int OffInterfaceIndex = 28;
    private const int OffPhysicalAddress = 40;
    private const int OffPhysicalLength = 72;
    private const int OffState = 76;
    private const ushort AfInet = 2;

    // NL_NEIGHBOR_STATE: 0 unreachable, 1 incomplete, 2 probe, 3 delay, 4 stale, 5 reachable, 6 permanent.
    private const int StateProbe = 2;
    private const int StateReachable = 5;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpNetTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destIp, uint srcIp, [Out] byte[] macAddr, ref uint physAddrLen);

    /// <summary>
    /// Devices Windows has resolved on one adapter recently. Leaves out the permanent entries the DHCP server
    /// adds for its own clients and anything without a MAC.
    /// </summary>
    public static IReadOnlyList<ArpEntry> Read(int interfaceIndex)
    {
        var result = new List<ArpEntry>();
        if (GetIpNetTable2(AfInet, out var table) != 0 || table == IntPtr.Zero) return result;
        try
        {
            int count = Marshal.ReadInt32(table);
            var mac = new byte[6];
            for (int i = 0; i < count; i++)
            {
                var row = table + TableHeader + i * RowSize;
                if (Marshal.ReadInt16(row) != AfInet || Marshal.ReadInt32(row, OffInterfaceIndex) != interfaceIndex) continue;
                int state = Marshal.ReadInt32(row, OffState);
                if (state is < StateProbe or > StateReachable || Marshal.ReadInt32(row, OffPhysicalLength) != 6) continue;
                Marshal.Copy(row + OffPhysicalAddress, mac, 0, 6);
                var m = MacAddress.FromBytes(mac);
                if (m == MacAddress.Empty || m.IsBroadcast) continue;
                var ipBytes = new byte[4];
                Marshal.Copy(row + 4, ipBytes, 0, 4);
                result.Add(new ArpEntry(new IPAddress(ipBytes), m, interfaceIndex));
            }
        }
        finally
        {
            FreeMibTable(table);
        }
        return result;
    }

    /// <summary>Sends an ARP request from <paramref name="source"/> and waits (up to about 3 s) for the answer.</summary>
    public static MacAddress? Probe(IPAddress target, IPAddress source)
    {
        var mac = new byte[8];
        uint length = (uint)mac.Length;
        int rc = SendARP(ToNetwork(target), ToNetwork(source), mac, ref length);
        if (rc != 0 || length < 6) return null;
        var m = MacAddress.FromBytes(mac);
        return m == MacAddress.Empty ? null : m;
    }

    // SendARP blocks its thread, so each probe gets its own; the sweep runs a fixed number of them at once.
    public Task<MacAddress?> ProbeAsync(IPAddress target, IPAddress source, CancellationToken ct) =>
        Task.Factory.StartNew(() => Probe(target, source), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static uint ToNetwork(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
}
