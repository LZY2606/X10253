using System.Globalization;

namespace PowerSeq.Core.Model;

/// <summary>
/// 带原始表示的量。RawText 永不丢弃；无法规范化时 Canonical 为 null 并附诊断。
/// </summary>
public sealed record Qty(string RawText, double? Value, string? Unit)
{
    public bool IsConvertible => Value.HasValue;
    public double RequireValue() => Value ?? throw new InvalidOperationException($"quantity '{RawText}' 无法规范化");

    public static Qty Parse(string raw)
    {
        var text = raw.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([A-Za-zμµ%\/·0-9²³]*)$");
        if (!m.Success || string.IsNullOrWhiteSpace(m.Groups[1].Value))
            return new Qty(text, null, null);
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var u = m.Groups[2].Value.Trim();
        return new Qty(text, v, u.Length == 0 ? null : u);
    }

    public override string ToString() => RawText;
}

/// <summary>单位换算表：派生层才做规范化；无法转换则留原值。</summary>
public static class UnitTable
{
    // 归一到基本单位：V, s, V（测量误差）。
    private static readonly Dictionary<string, double> Voltage = new()
    {
        ["V"] = 1.0, ["v"] = 1.0, ["mV"] = 1e-3, ["mv"] = 1e-3, ["uV"] = 1e-6, ["µV"] = 1e-6
    };

    private static readonly Dictionary<string, double> Time = new()
    {
        ["s"] = 1.0, ["sec"] = 1.0, ["ms"] = 1e-3, ["us"] = 1e-6, ["µs"] = 1e-6, ["ns"] = 1e-9
    };

    public static bool IsVoltageUnit(string? unit) => unit is not null && Voltage.ContainsKey(unit);
    public static bool IsTimeUnit(string? unit) => unit is not null && Time.ContainsKey(unit);

    public static double? ToCanonicalVoltage(Qty q)
        => q is { Value: not null, Unit: not null } && Voltage.TryGetValue(q.Unit, out var f)
            ? q.Value.Value * f
            : q is { Value: not null, Unit: null } ? q.Value : null;

    public static double? ToCanonicalTime(Qty q)
        => q is { Value: not null, Unit: not null } && Time.TryGetValue(q.Unit, out var f)
            ? q.Value.Value * f
            : q is { Value: not null, Unit: null } ? q.Value : null;
}
