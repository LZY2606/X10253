using System.Globalization;

namespace PowerSeq.Core;

/// <summary>
/// 派生层规范化：把原始字符串采样（带单位）转换为 ns / V。
/// 无法转换的值不丢、不改写，留在原位置并附诊断。
/// </summary>
public static class Waveform
{
    public static readonly IReadOnlyDictionary<string, double> TimeUnitToNs =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ns"] = 1.0, ["us"] = 1_000.0, ["µs"] = 1_000.0,
            ["ms"] = 1_000_000.0, ["s"] = 1_000_000_000.0
        };

    public static readonly IReadOnlyDictionary<string, double> VoltUnitToV =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["V"] = 1.0, ["mV"] = 0.001, ["uV"] = 0.000_001
        };

    public sealed record NormalizeResult(
        IReadOnlyList<Sample?> Samples,           // 与原采样一一对应；失败位置为 null
        IReadOnlyList<ImportDiagnostic> Diagnostics);

    public static NormalizeResult Normalize(string sourceId, IReadOnlyList<RawSample> raw)
    {
        var samples = new Sample?[raw.Count];
        var diags = new List<ImportDiagnostic>();
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var okT = double.TryParse(r.RawTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
            var okV = double.TryParse(r.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
            if (!okT || !okV)
            {
                diags.Add(new ImportDiagnostic(sourceId, i, r.RawTime, r.RawValue,
                    !okT ? $"time '{r.RawTime}' is not a number" : $"value '{r.RawValue}' is not a number"));
                continue;
            }
            if (!TimeUnitToNs.TryGetValue(r.TimeUnit, out var tf))
            {
                diags.Add(new ImportDiagnostic(sourceId, i, r.RawTime, r.RawValue, $"unknown time unit '{r.TimeUnit}'"));
                continue;
            }
            if (!VoltUnitToV.TryGetValue(r.ValueUnit, out var vf))
            {
                diags.Add(new ImportDiagnostic(sourceId, i, r.RawTime, r.RawValue, $"unknown value unit '{r.ValueUnit}'"));
                continue;
            }
            samples[i] = new Sample(t * tf, v * vf);
        }
        return new NormalizeResult(samples, diags);
    }

    /// <summary>可用采样（跳过无法转换的位置），按时间升序。</summary>
    public static IReadOnlyList<Sample> UsableSamples(NormalizeResult result) =>
        result.Samples.Where(s => s is not null).Select(s => s!).OrderBy(s => s.TimeNs).ToList();
}
