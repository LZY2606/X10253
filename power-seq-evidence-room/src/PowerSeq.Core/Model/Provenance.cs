using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSeq.Core.Model;

/// <summary>
/// 直接来源：每个派生值都记录直接来源坐标（而非笼统来源）。
/// Coordinates 例：{"capture":"cap-0001","column":3,"index":17}。
/// </summary>
public sealed record SourceRef(string CaptureId, string ConstraintId, IReadOnlyDictionary<string, string> Coordinates)
{
    public static SourceRef Of(string capture, string constraint, params (string k, string v)[] coords)
        => new(capture, constraint, new Dictionary<string, string>(coords.Select(c => KeyValuePair.Create(c.k, c.v))));
}

/// <summary>算法版本 + 参数指纹 + 直接来源，附加到任何派生对象。</summary>
public sealed record Derivation(
    string AlgorithmVersion,
    string ParameterFingerprint,
    IReadOnlyList<SourceRef> DirectSources)
{
    public const string CurrentAlgorithmVersion = "seq-engine-1.0.0";
}

public static class Fingerprint
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static string Sha256(string canonicalJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Of(object value) => Sha256(JsonSerializer.Serialize(value, Opts));

    /// <summary>稳定身份：在重放/导出再导入后保持不变（内容寻址）。</summary>
    public static string Stable(string kind, string? parent, string body)
        => Sha256($"{kind}|{parent ?? "-"}|{body}")[..16];
}
