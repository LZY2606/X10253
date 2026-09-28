using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSequencing.Core;

/// <summary>
/// Content-addressed stable identities. Every derived value hashes the algorithm version,
/// parameter fingerprint and direct sources, so export -> delete -> import preserves the same id.
/// </summary>
public static class StableId
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public static string For(string prefix, object canonical)
    {
        var json = JsonSerializer.Serialize(canonical, CanonicalOptions);
        return ForText(prefix, json);
    }

    public static string ForText(string prefix, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{prefix}_{Convert.ToHexString(bytes, 0, 16).ToLowerInvariant()}";
    }

    public static string Fingerprint(object canonical) => ForText("fp", JsonSerializer.Serialize(canonical, CanonicalOptions));
}
