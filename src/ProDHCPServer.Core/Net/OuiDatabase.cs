using System.Globalization;
using System.IO.Compression;

namespace ProDHCPServer.Core.Net;

/// <summary>
/// Offline manufacturer lookup from the IEEE MA-L registry, bundled as a gzip'd
/// "AABBCC&lt;tab&gt;Name" resource. Loaded on first use.
/// </summary>
public sealed class OuiDatabase
{
    private static readonly Lazy<OuiDatabase> BuiltIn = new(LoadBuiltIn);
    private readonly Dictionary<uint, string> _entries;

    private OuiDatabase(Dictionary<uint, string> entries) => _entries = entries;

    public static OuiDatabase Default => BuiltIn.Value;

    public int Count => _entries.Count;

    public static OuiDatabase Parse(TextReader reader)
    {
        var entries = new Dictionary<uint, string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int tab = line.IndexOf('\t');
            if (tab != 6) continue;
            if (uint.TryParse(line.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var oui))
                entries[oui] = line[(tab + 1)..].Trim();
        }
        return new OuiDatabase(entries);
    }

    private static OuiDatabase LoadBuiltIn()
    {
        using var stream = typeof(OuiDatabase).Assembly.GetManifestResourceStream("ProDHCPServer.Core.oui.txt.gz")
            ?? throw new InvalidOperationException("The bundled manufacturer list is missing from the build.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return Parse(reader);
    }

    /// <summary>The manufacturer name, a note for randomised MACs, or null when unknown.</summary>
    public string? Lookup(MacAddress mac)
    {
        if (_entries.TryGetValue(mac.Oui, out var name)) return name;
        if (mac.IsLocallyAdministered) return "Private / randomised MAC";
        return null;
    }
}
