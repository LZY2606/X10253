using System.Security.Cryptography;
using System.Text;

namespace PowerSequencing.Core;

/// <summary>
/// Deterministic, content-addressed stable identities.  No randomness is used
/// anywhere in the production pipeline; a fixed seed only feeds tests that
/// perturb generated sample files.  The same materials therefore always carry
/// the same id, across databases, exports and re-imports.
/// </summary>
public static class StableId
{
    public static string Of(string kind, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\u0000" + content));
        return kind[..3].ToLowerInvariant() + "-" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string Of(string kind, IEnumerable<string> parts) =>
        Of(kind, string.Join("\u0001", parts));
}

/// <summary>Virtual time source so replay and tests never depend on wall clock.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class FixedClock(DateTimeOffset at) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = at;
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    public void Set(DateTimeOffset at) => UtcNow = at;
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
