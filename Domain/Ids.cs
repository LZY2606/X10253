using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerSequencer.Domain;

/// <summary>Content-addressed stable identities. Same content => same id, across export/import.</summary>
public static class Ids
{
    public static string Of(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string OfNode(JsonNode node) => Of(Canonical(node));

    /// <summary>Canonical JSON: object keys sorted recursively, compact separators.</summary>
    public static string Canonical(JsonNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) sb.Append(JsonSerializer.Serialize(s));
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<double>(out var d)) sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(v.ToJsonString());
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i], sb); }
                sb.Append(']');
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, val) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(val, sb);
                }
                sb.Append('}');
                break;
        }
    }
}
