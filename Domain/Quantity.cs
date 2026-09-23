using System.Globalization;

namespace PSE.Domain;

/// <summary>
/// 一个带坐标基准/单位的领域量。原始表示（Raw 与 Unit）始终保留；
/// 只有能解析时才填充规范化值 Value（SI 基准：V、s、Hz）。
/// 无法转换的值留在原位置并附诊断，绝不为画图而丢弃。
/// </summary>
public sealed record Quantity(string Raw, string Unit, double? Value)
{
    public static readonly IReadOnlyDictionary<string, double> SiFactors = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["V"] = 1.0, ["mV"] = 1e-3, ["uV"] = 1e-6,
        ["s"] = 1.0, ["ms"] = 1e-3, ["us"] = 1e-6, ["ns"] = 1e-9,
        ["Hz"] = 1.0, ["kHz"] = 1e3, ["MHz"] = 1e6,
        [""] = 1.0,
    };

    public static Quantity Parse(string? raw, string? unit)
    {
        var r = raw?.Trim() ?? "";
        var u = unit?.Trim() ?? "";
        double? value = null;
        if (double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            SiFactors.TryGetValue(u, out var factor))
        {
            value = parsed * factor;
        }
        return new Quantity(r, u, value);
    }

    /// <summary>未规范化（单位未知或无法解析）时返回 true，调用方必须保留原位置诊断。</summary>
    public bool IsUnconverted => Value is null;

    public override string ToString() => $"{Raw} {Unit}".Trim();
}
