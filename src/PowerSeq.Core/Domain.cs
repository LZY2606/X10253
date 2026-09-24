using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerSeq.Core;

/// <summary>虚拟时钟：应用与测试注入，测试用固定时间，绝不依赖真实墙钟。</summary>
public interface IClock
{
    DateTimeOffset Now();
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}

/// <summary>手动推进的虚拟时钟，测试专用。</summary>
public sealed class ManualClock : IClock
{
    private DateTimeOffset _now;
    public ManualClock(DateTimeOffset start) => _now = start;
    public DateTimeOffset Now() => _now;
    public void Advance(TimeSpan dt) => _now = _now.Add(dt);
}

public enum RailState { Unknown = 0, Bad = 1, Good = 2 }

public enum SourceKind { RailVoltage, EnableSignal, GoodSignal }

public enum Verdict { Accept, Reject, Adjust }

/// <summary>原始采样点：导入层保留原始字符串表示与单位，绝不在导入时规范化。</summary>
public sealed record RawSample(
    [property: JsonPropertyName("t")] string RawTime,
    [property: JsonPropertyName("v")] string RawValue,
    [property: JsonPropertyName("tUnit")] string TimeUnit,
    [property: JsonPropertyName("vUnit")] string ValueUnit);

/// <summary>规范化后的采样点：时间 ns、电压 V。</summary>
public sealed record Sample(double TimeNs, double Volts);

/// <summary>无法转换的值留在原位置并附诊断。</summary>
public sealed record ImportDiagnostic(string SourceId, int SampleIndex, string RawTime, string RawValue, string Reason);

public sealed record ClockSpec(
    double NominalPeriodNs,
    double GapFactor,
    double SimultaneityWindowNs,
    double MeasurementErrorV);

/// <summary>单轨阈值版本：上升与下降使用不同阈值（滞回），最短稳定时间区分 glitch 与违约。</summary>
public sealed record RailThreshold(
    string Rail,
    double RiseThresholdV,
    double FallThresholdV,
    double MinStableNs)
{
    public void Validate()
    {
        if (FallThresholdV >= RiseThresholdV)
            throw new FormatException($"rail {Rail}: fall threshold {FallThresholdV} must be < rise threshold {RiseThresholdV} (hysteresis band)");
        if (MinStableNs <= 0)
            throw new FormatException($"rail {Rail}: min stable time must be positive");
    }
}

public sealed record ThresholdPayload(IReadOnlyList<RailThreshold> Rails);

public sealed record DependencyEdge(string FromRail, string ToRail)
{
    public override string ToString() => $"{FromRail}->{ToRail}";
}

public sealed record DependencyPayload(IReadOnlyList<DependencyEdge> Edges);

public static class Ids
{
    /// <summary>内容寻址的稳定身份：同一内容在任何机器、任何时刻得到同一 ID。</summary>
    public static string ContentId(string prefix, string canonicalPayload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return $"{prefix}_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public static string Fingerprint<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonUtil.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Canon<T>(T value) => JsonSerializer.Serialize(value, JsonUtil.Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new FormatException($"cannot parse {typeof(T).Name}");
}
