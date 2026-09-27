using System.Security.Cryptography;
using System.Text;

namespace PowerEvidence.Domain;

/// <summary>确定性身份生成：固定 seed + 单调计数，重放/导出再导入后身份不变。</summary>
public sealed class IdGen
{
    private readonly string _seed;
    private int _counter;

    public IdGen(string seed) => _seed = seed;

    public string Next(string prefix) => $"{prefix}-{_seed}-{++_counter:x6}";

    public static string ContentId(string prefix, string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return $"{prefix}-{hash[..16].ToLowerInvariant()}";
    }
}

/// <summary>虚拟时钟：应用用系统时间，测试用固定时间，绝不访问真实时钟之外的源。</summary>
public interface ITimeSource
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTime : ITimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class VirtualTime : ITimeSource
{
    public VirtualTime(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; private set; }
    public void Advance(TimeSpan delta) => UtcNow += delta;
}
