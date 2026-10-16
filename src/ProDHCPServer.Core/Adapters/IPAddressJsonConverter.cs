using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProDHCPServer.Core.Adapters;

public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IPAddress.Parse(reader.GetString() ?? throw new JsonException("Expected an IP address string."));

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class IPAddressListJsonConverter : JsonConverter<IReadOnlyList<IPAddress>>
{
    public override IReadOnlyList<IPAddress> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<IPAddress>();
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of IP addresses.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            list.Add(IPAddress.Parse(reader.GetString() ?? throw new JsonException("Expected an IP address string.")));
        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<IPAddress> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var ip in value) writer.WriteStringValue(ip.ToString());
        writer.WriteEndArray();
    }
}
