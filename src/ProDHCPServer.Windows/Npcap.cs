using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProDHCPServer.Windows;

/// <summary>
/// Captures ARP, LLDP and CDP frames with Npcap, when the user has installed it (it isn't bundled).
/// The library is loaded at run time, so everything else works without it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class NpcapCapture : IDisposable
{
    /// <summary>Only the frames discovery needs, filtered in the driver so busy video traffic never reaches this app.</summary>
    public const string Filter = "arp or ether proto 0x88cc or ether dst 01:00:0c:cc:cc:cc";

    private static readonly Lazy<Api?> Library = new(Load);
    private readonly string _adapterId;
    private readonly Action<byte[]> _onFrame;
    private IntPtr _handle;
    private Thread? _thread;
    private volatile bool _stop;

    public NpcapCapture(string adapterId, Action<byte[]> onFrame)
    {
        _adapterId = adapterId;
        _onFrame = onFrame;
    }

    /// <summary>Raised once if capture stops with an error. Worker thread.</summary>
    public event Action<string>? Failed;

    public static bool IsInstalled => Library.Value is not null;

    /// <summary>Starts capturing. Returns a plain-English problem instead of throwing.</summary>
    public string? Start()
    {
        var api = Library.Value;
        if (api is null) return "Npcap isn't installed.";
        var name = Marshal.StringToHGlobalAnsi(@"\Device\NPF_" + (_adapterId.StartsWith('{') ? _adapterId : "{" + _adapterId + "}"));
        var filter = Marshal.StringToHGlobalAnsi(Filter);
        var errbuf = Marshal.AllocHGlobal(512);
        var program = Marshal.AllocHGlobal(16); // struct bpf_program { u_int bf_len; struct bpf_insn *bf_insns; }
        try
        {
            var p = api.Create(name, errbuf);
            if (p == IntPtr.Zero) return $"Npcap couldn't open this adapter: {Marshal.PtrToStringAnsi(errbuf)}";
            api.SetSnaplen(p, 2048);
            api.SetPromisc(p, 1); // LLDP and CDP go to multicast MACs the adapter would otherwise drop
            api.SetTimeout(p, 250);
            api.SetImmediate(p, 1);
            int rc = api.Activate(p);
            if (rc < 0)
            {
                var err = Marshal.PtrToStringAnsi(api.GetErr(p));
                api.Close(p);
                return $"Npcap couldn't start capturing on this adapter ({err}).";
            }
            if (api.Compile(p, program, filter, 1, 0xFFFFFFFF) != 0 || api.SetFilter(p, program) != 0)
            {
                var err = Marshal.PtrToStringAnsi(api.GetErr(p));
                api.Close(p);
                return $"Npcap rejected the capture filter ({err}).";
            }
            api.FreeCode(program);
            _handle = p;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(errbuf);
            Marshal.FreeHGlobal(program);
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Npcap capture" };
        _thread.Start();
        return null;
    }

    private void Loop()
    {
        var api = Library.Value!;
        while (!_stop)
        {
            IntPtr header, data;
            int rc = api.NextEx(_handle, &header, &data);
            if (rc == 1)
            {
                int caplen = *(int*)(header + 8); // struct pcap_pkthdr { struct timeval ts (2 x 32-bit); bpf_u_int32 caplen; bpf_u_int32 len; }
                if (caplen <= 0 || caplen > 65535) continue;
                var frame = new byte[caplen];
                Marshal.Copy(data, frame, 0, caplen);
                try { _onFrame(frame); } catch { /* a bad frame must not stop the capture */ }
            }
            else if (rc < 0)
            {
                if (!_stop) Failed?.Invoke($"Npcap capture stopped: {Marshal.PtrToStringAnsi(api.GetErr(_handle))}");
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        if (_handle != IntPtr.Zero) { Library.Value?.Close(_handle); _handle = IntPtr.Zero; }
    }

    private static Api? Load()
    {
        // Npcap installs to System32\Npcap; in WinPcap-compatible mode it's also in System32.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var dir in new[] { Path.Combine(system, "Npcap"), system })
        {
            var wpcap = Path.Combine(dir, "wpcap.dll");
            if (!File.Exists(wpcap)) continue;
            try
            {
                // wpcap.dll needs Packet.dll from the same folder; load it first by full path.
                var packet = Path.Combine(dir, "Packet.dll");
                if (File.Exists(packet)) NativeLibrary.Load(packet);
                var lib = NativeLibrary.Load(wpcap);
                return new Api(lib);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                continue;
            }
        }
        return null;
    }

    private sealed class Api(IntPtr lib)
    {
        public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Create = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(lib, "pcap_create");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> SetSnaplen = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pcap_set_snaplen");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> SetPromisc = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pcap_set_promisc");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> SetTimeout = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pcap_set_timeout");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> SetImmediate = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pcap_set_immediate_mode");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, int> Activate = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(lib, "pcap_activate");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, uint, int> Compile = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, uint, int>)NativeLibrary.GetExport(lib, "pcap_compile");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> SetFilter = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(lib, "pcap_setfilter");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, void> FreeCode = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "pcap_freecode");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr*, int> NextEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr*, int>)NativeLibrary.GetExport(lib, "pcap_next_ex");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> GetErr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(lib, "pcap_geterr");
        public readonly delegate* unmanaged[Cdecl]<IntPtr, void> Close = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "pcap_close");
    }
}
