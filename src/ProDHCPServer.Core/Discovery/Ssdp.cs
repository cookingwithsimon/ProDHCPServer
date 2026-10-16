using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ProDHCPServer.Core.Discovery;

/// <summary>An SSDP search response or NOTIFY announcement (UPnP device discovery).</summary>
public sealed record SsdpMessage(bool IsNotify, IReadOnlyDictionary<string, string> Headers)
{
    public string? Location => Header("LOCATION");
    public string? Server => Header("SERVER");
    public string? Usn => Header("USN");
    /// <summary>The device or service type: ST in responses, NT in announcements.</summary>
    public string? Type => Header(IsNotify ? "NT" : "ST");
    public bool IsByeBye => IsNotify && string.Equals(Header("NTS"), "ssdp:byebye", StringComparison.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

public static class Ssdp
{
    public const int Port = 1900;
    public static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");

    /// <summary>A search for every UPnP device ("ssdp:all"). Devices reply to the sender within MX seconds.</summary>
    public static byte[] BuildSearch(string target = "ssdp:all", int maxWaitSeconds = 2) => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {Group}:{Port}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"MX: {maxWaitSeconds}\r\n" +
        $"ST: {target}\r\n" +
        "USER-AGENT: Windows/10 UPnP/1.1 ProDHCPServer/1\r\n\r\n");

    /// <summary>Parses a search response ("HTTP/1.1 200 OK") or NOTIFY. Searches from other hosts return null.</summary>
    public static SsdpMessage? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 12 or > 8192) return null;
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split("\r\n");
        bool notify = lines[0].StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase);
        bool response = lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) && lines[0].Contains(" 200", StringComparison.Ordinal);
        if (!notify && !response) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        return new SsdpMessage(notify, headers);
    }

    /// <summary>
    /// The LOCATION URL, if it's a plain http URL on the device that sent the message (so fetching
    /// it can't be used to make this laptop contact some other host).
    /// </summary>
    public static Uri? SafeLocation(SsdpMessage message, IPAddress sender)
    {
        if (!Uri.TryCreate(message.Location, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp) return null;
        return IPAddress.TryParse(uri.Host, out var host) && host.Equals(sender) ? uri : null;
    }
}

/// <summary>The useful parts of a UPnP device description (the XML at an SSDP LOCATION).</summary>
public sealed record UpnpDescription(string? FriendlyName, string? Manufacturer, string? ModelName, string? ModelNumber, string? SerialNumber, string? PresentationUrl)
{
    public const int MaxBytes = 256 * 1024;

    public string? Model => (ModelName, ModelNumber) switch
    {
        ({ } name, { } number) when !name.Contains(number, StringComparison.OrdinalIgnoreCase) => $"{name} {number}",
        ({ } name, _) => name,
        (_, var number) => number,
    };

    public static UpnpDescription? TryParse(string xml)
    {
        if (xml.Length > MaxBytes) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;
            string? Get(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 and <= 200 } v ? v : null;
            return new UpnpDescription(Get("friendlyName"), Get("manufacturer"), Get("modelName"), Get("modelNumber"), Get("serialNumber"), Get("presentationURL"));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The device's web page, made absolute against the description URL. Only http(s) on the same host.</summary>
    public Uri? WebPage(Uri descriptionUrl)
    {
        if (PresentationUrl is null || !Uri.TryCreate(descriptionUrl, PresentationUrl, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Host == descriptionUrl.Host ? uri : null;
    }
}
