using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerEvidenceRoom.Domain;

public static class Identity
{
    public static string Fingerprint(JsonNode? node)
    {
        var canonical = CanonicalJson(node);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string Fingerprint(IEnumerable<string> parts)
    {
        using var sha = SHA256.Create();
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    public static string Short(string fingerprint) => fingerprint[..12];

    public static string CanonicalJson(JsonNode? node) =>
        JsonSerializer.Serialize(Canonical(node), JsonOptions);

    private static JsonNode? Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                result[property.Key] = Canonical(property.Value);
            }
            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(Canonical(item));
            }
            return result;
        }

        return node?.DeepClone();
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public sealed class ImportFormatException : Exception
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public ImportFormatException(IReadOnlyList<Diagnostic> diagnostics)
        : base(string.Join("; ", diagnostics.Select(d => d.Message)))
    {
        Diagnostics = diagnostics;
    }
}
