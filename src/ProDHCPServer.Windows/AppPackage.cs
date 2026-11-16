using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProDHCPServer.Windows;

/// <summary>Tells a Microsoft Store (MSIX) install apart from the downloaded exe.</summary>
[SupportedOSPlatform("windows")]
public static partial class AppPackage
{
    private const int AppModelErrorNoPackage = 15700;

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    /// <summary>
    /// True when this copy came from an MSIX package, so the Store (not the app) installs updates and the exe
    /// can't be replaced. The path check covers the elevated relaunch, which can start without package identity.
    /// </summary>
    public static bool IsPackaged { get; } = HasPackageIdentity() || RunsFromWindowsApps(Environment.ProcessPath);

    private static bool HasPackageIdentity()
    {
        int length = 0;
        try { return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool RunsFromWindowsApps(string? exe) =>
        exe is not null && exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
}
