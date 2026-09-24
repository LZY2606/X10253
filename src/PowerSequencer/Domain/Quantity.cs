using System.Globalization;

namespace PowerSeq.Domain;

/// <summary>
/// 领域量：始终携带数值、单位与原始表示。
/// 导入层保留 Raw；派生层才调用 ToCanonical 做规范化换算。
/// 无法换算的值保留在原位置并附诊断，绝不为了画图丢弃。
/// </summary>
public sealed record Quantity(double Value, string Unit, string Raw)
{
    public const string VoltageCanonical = "V";
    public const string TimeCanonical = "ms";

    private static readonly Dictionary<string, (string Dim, double Factor)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"] = (VoltageCanonical, 1.0),
        ["mV"] = (VoltageCanonical, 0.001),
        ["ms"] = (TimeCanonical, 1.0),
        ["us"] = (TimeCanonical, 0.001),
        ["µs"] = (TimeCanonical, 0.001),
        ["s"] = (TimeCanonical, 1000.0),
    };

    /// <summary>单位换算表（导入层只读查询，不做隐式换算）。</summary>
    public static IReadOnlyDictionary<string, (string Dim, double Factor)> Table0 => Table;

    public static bool TryParse(string raw, out Quantity quantity, out string? diagnostic)
    {
        quantity = new Quantity(0, "", raw);
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostic = $"空量值: '{raw}'";
            return false;
        }
        var trimmed = raw.Trim();
        var split = trimmed.Length;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (!(char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E'))
            {
                split = i;
                break;
            }
        }
        var numPart = trimmed[..split].Trim();
        var unitPart = trimmed[split..].Trim();
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            diagnostic = $"无法解析数值: '{raw}'";
            return false;
        }
        if (unitPart.Length == 0)
        {
            diagnostic = $"缺少单位: '{raw}'";
            return false;
        }
        if (!Table.ContainsKey(unitPart))
        {
            quantity = new Quantity(value, unitPart, raw);
            diagnostic = $"未知单位 '{unitPart}'，原值保留在 '{raw}'，不参与规范化计算";
            return false;
        }
        quantity = new Quantity(value, unitPart, raw);
        return true;
    }

    /// <summary>派生层规范化：换算到 canonical 单位。失败时返回 null 并给出诊断。</summary>
    public double? ToCanonical(string dimension, out string? diagnostic)
    {
        diagnostic = null;
        if (!Table.TryGetValue(Unit, out var entry))
        {
            diagnostic = $"未知单位 '{Unit}'（原始表示 '{Raw}'），无法换算为 {dimension}";
            return null;
        }
        if (entry.Dim != dimension)
        {
            diagnostic = $"量纲不符: '{Unit}' 不是 {dimension}（原始表示 '{Raw}'）";
            return null;
        }
        return Value * entry.Factor;
    }

    public double CanonicalOrThrow(string dimension) =>
        ToCanonical(dimension, out var d) ?? throw new InvalidOperationException(d);

    public override string ToString() => Raw;
}
