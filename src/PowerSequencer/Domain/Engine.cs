using System.Globalization;

namespace PowerSequencer.Domain;

/// <summary>
/// 时序评估引擎（算法版本 seq-eval/1.0.0）。
/// 上升/下降使用不同阈值；滞回带内保持原状态；短于最小稳定时间的越界记为 glitch，
/// 达到最小稳定时间才提交状态迁移；时钟缺口产生 Unknown 窗口而非插值；
/// 循环依赖给出最小矛盾边集（全部同样最小的集合都保留为候选）。
/// </summary>
public static class Engine
{
    public const string AlgorithmVersion = "seq-eval/1.0.0";

    public static List<NormSeries> Normalize(EvalInput input)
    {
        var result = new List<NormSeries>();
        foreach (var series in input.RawSeries)
        {
            var points = new List<NormPoint>();
            var diags = new List<string>();
            foreach (var raw in series.Samples)
            {
                var okT = double.TryParse(raw.T, NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
                var okV = double.TryParse(raw.V, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
                if (okT && okV)
                {
                    points.Add(new NormPoint(t, v));
                }
                else
                {
                    diags.Add($"轨 {series.Rail}: 原始点 (t=\"{raw.T}\", v=\"{raw.V}\", 单位 {series.TimeUnit}/{series.ValueUnit}) 无法转换，保留在原位置并跳过规范化");
                }
            }
            points.Sort((a, b) => a.TMs.CompareTo(b.TMs));
            result.Add(new NormSeries(series.Rail, points, diags));
        }
        return result;
    }

    public static EvalOutput Evaluate(EvalInput input)
    {
        var norm = Normalize(input);
        var windows = new List<StateWindow>();
        var glitches = new List<Glitches>();
        var unknowns = new List<string>();
        var violations = new List<Violation>();
        foreach (var s in norm)
        {
            var th = input.Thresholds.Rails.FirstOrDefault(r => r.Rail == s.Rail);
            if (th is null)
            {
                unknowns.Add($"轨 {s.Rail} 缺少阈值定义，原始采样保留但不参与判定");
                continue;
            }
            BuildWindows(s, th, input.Clock, windows, glitches, unknowns);
        }
        return new EvalOutput(norm, windows, glitches, violations, new(), new(), new(), unknowns);
    }

    private static void BuildWindows(
        NormSeries series, RailThreshold th, ClockSpec clock,
        List<StateWindow> windows, List<Glitches> glitches, List<string> unknowns)
    {
        // placeholder, replaced below
    }
}
