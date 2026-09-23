using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerTiming.Core.Domain;

/// <summary>
/// Deterministic stable identities. Every identity survives export/delete/re-import
/// because it is a prefix-bounded hash of canonical content, never a random guid.
/// </summary>
public static class Hashing
{
    public const string FixedSeedSalt = "power-timing-evidence-room-seed-1224";

    public static string StableId(string kind, string content, int length = 16)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{FixedSeedSalt}|{kind}|{content}"));
        return $"{kind}_{Convert.ToHexString(bytes).Substring(0, length).ToLowerInvariant()}";
    }

    public static string Fingerprint(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(element, writer);
        }

        var hash = SHA256.HashData(stream.ToArray());
        return "fp_" + Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
    }

    public static string Fingerprint(string canonicalJson)
    {
        using var doc = JsonDocument.Parse(canonicalJson);
        return Fingerprint(doc.RootElement.Clone());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(element.GetRawText());
                break;
        }
    }
}
