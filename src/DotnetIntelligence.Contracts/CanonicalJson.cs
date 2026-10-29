using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Contracts;

/// <summary>
/// The single JSON writer for every artifact: object keys sorted ordinally, no insignificant
/// whitespace, UTF-8 without BOM, '\n' line endings for NDJSON. Same input, same bytes, on any host.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] SerializeToUtf8(JsonNode? node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, node);
        }
        return buffer.ToArray();
    }

    public static string Serialize(JsonNode? node) => Utf8NoBom.GetString(SerializeToUtf8(node));

    /// <summary>Writes one canonical JSON document per line, in the order given, each terminated by '\n'.</summary>
    public static void WriteNdjson(Stream stream, IEnumerable<JsonNode> records)
    {
        foreach (var record in records)
        {
            var bytes = SerializeToUtf8(record);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
        }
    }

    /// <summary>Writes a canonical JSON document followed by a single '\n'.</summary>
    public static void WriteDocument(string path, JsonNode node)
    {
        using var stream = File.Create(path);
        stream.Write(SerializeToUtf8(node));
        stream.WriteByte((byte)'\n');
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteValue(writer, value);
                break;
            default:
                throw new NotSupportedException($"Unsupported JSON node type {node.GetType().Name}.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        if (value.TryGetValue(out string? s))
        {
            writer.WriteStringValue(s);
        }
        else if (value.TryGetValue(out bool b))
        {
            writer.WriteBooleanValue(b);
        }
        else if (value.TryGetValue(out long l))
        {
            writer.WriteNumberValue(l);
        }
        else if (value.TryGetValue(out int i))
        {
            writer.WriteNumberValue(i);
        }
        else if (value.TryGetValue(out double d))
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new NotSupportedException("Non-finite numbers are not allowed in artifacts.");
            }
            writer.WriteNumberValue(d);
        }
        else if (value.TryGetValue(out JsonElement element))
        {
            WriteElement(writer, element);
        }
        else
        {
            throw new NotSupportedException($"Unsupported JSON value '{value}'.");
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                Write(writer, JsonNode.Parse(element.GetRawText()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
