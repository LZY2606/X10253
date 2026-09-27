using System.Globalization;
using System.Text.Json.Serialization;

namespace PowerSeqEvidenceRoom.Domain;

/// <summary>
/// 带单位/坐标基准的量值。导入层保留原始字符串表示与原始单位，
/// 派生层才做规范化；无法转换时 ValueV 为空并带诊断。
/// </summary>
public sealed record Quantity
{
    [JsonPropertyName("raw")] public required string Raw { get; init; }
    [JsonPropertyName("unit")] public required string Unit { get; init; }
    [JsonPropertyName("baseUnit")] public required string BaseUnit { get; init; }
    [JsonPropertyName("valueBase")] public double? ValueBase { get; init; }
    [JsonPropertyName("diagnostic")] public string? Diagnostic { get; init; }

    public bool IsConvertible => ValueBase.HasValue;

    public static Quantity Volts(string raw, double? v) => new()
    {
        Raw = raw, Unit = "V", BaseUnit = "V",
        ValueBase = v,
        Diagnostic = v is null ? $"无法把 '{raw}' 转换为伏特(V)，保留原表示" : null
    };

    public static Quantity Seconds(string raw, double? v) => new()
    {
        Raw = raw, Unit = raw.ExtractUnit() ?? "s", BaseUnit = "s",
        ValueBase = v,
        Diagnostic = v is null ? $"无法把 '{raw}' 转换为秒(s)，保留原表示" : null
    };

    public static Quantity Unitless(string raw, double? v) => new()
    {
        Raw = raw, Unit = "", BaseUnit = "",
        ValueBase = v,
        Diagnostic = v is null ? $"无法解析数值 '{raw}'，保留原表示" : null
    };

    public string Display() => IsConvertible
        ? string.Create(CultureInfo.InvariantCulture, $"{ValueBase:F6}{BaseUnit}")
        : $"{Raw}(未转换)";
}

public static class TextUtil
{
    public static string? ExtractUnit(this string raw)
    {
        var s = raw.Trim();
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        var u = s[i..].Trim();
        return u.Length == 0 ? null : u;
    }
}

/// <summary>电压/时间单位换算（显式表，不隐式吞掉无法识别的单位）。</summary>
public static class UnitConvert
{
    public static double? ToVolts(string raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        var num = ParseLeadingDouble(s, out var unit);
        if (num is null) return null;
        return unit.ToLowerInvariant() switch
        {
            "" or "v" => num,
            "mv" => num / 1000.0,
            "uv" => num / 1_000_000.0,
            _ => null
        };
    }

    public static double? ToSeconds(string raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        var num = ParseLeadingDouble(s, out var unit);
        if (num is null) return null;
        return unit.ToLowerInvariant() switch
        {
            "s" => num,
            "ms" => num / 1000.0,
            "us" or "µs" => num / 1_000_000.0,
            "ns" => num / 1_000_000_000.0,
            _ => null
        };
    }

    public static double? ToHertz(string raw)
    {
        var s = raw.Trim();
        var num = ParseLeadingDouble(s, out var unit);
        if (num is null) return null;
        return unit.ToLowerInvariant() switch
        {
            "" or "hz" => num,
            "khz" => num * 1000.0,
            "mhz" => num * 1_000_000.0,
            _ => null
        };
    }

    private static double? ParseLeadingDouble(string s, out string unit)
    {
        unit = "";
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        var head = s[..i];
        unit = s[i..].Trim();
        return double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
