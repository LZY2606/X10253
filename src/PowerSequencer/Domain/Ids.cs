using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSequencer.Domain;

/// <summary>稳定身份：由内容哈希派生，导出/删除/再导入后保持不变。</summary>
public static class Ids
{
    public static string New(string kind, string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return $"{kind}-{hash[..12]}";
    }

    public static string Canonical<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOpts.Default);
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public interface IClock
{
    DateTimeOffset Now();
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}

/// <summary>测试用虚拟时间：固定起点，手动推进。</summary>
public sealed class VirtualClock : IClock
{
    private DateTimeOffset _now;
    public VirtualClock(string start = "2026-01-01T00:00:00Z") => _now = DateTimeOffset.Parse(start);
    public DateTimeOffset Now() => _now;
    public void Advance(TimeSpan dt) => _now = _now.Add(dt);
}
