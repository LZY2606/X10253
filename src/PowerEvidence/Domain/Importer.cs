using System.Text.Json;

namespace PowerEvidence.Domain;

/// <summary>导入结果：类型化载荷 + 诊断。无法转换的值留在原位置并附诊断。</summary>
public sealed record ImportResult(
    string Kind,
    string? Name,
    string? ParentName,
    object? Payload,
    IReadOnlyList<string> Diagnostics)
{
    public bool Ok => Payload is not null;
}

public static class Importer
{
    public static readonly string[] Kinds = { "waveform-bundle", "threshold-set", "dependency-graph", "directives" };

    public static ImportResult Parse(string rawJson)
    {
        var diagnostics = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException e)
        {
            return new ImportResult("invalid", null, null, null,
                new[] { $"格式错误: JSON 无法解析: {e.Message}" });
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindEl))
            return new ImportResult("invalid", null, null, null,
                new[] { "格式错误: 缺少必需字段 'kind'" });

        var kind = kindEl.GetString() ?? "";
        if (!Kinds.Contains(kind))
            return new ImportResult("invalid", null, null, null,
                new[] { $"格式错误: 未知 kind '{kind}'，支持: {string.Join(", ", Kinds)}" });

        var name = TryString(root, "name");
        var parent = TryString(root, "parent");

        object? payload = kind switch
        {
            "waveform-bundle" => ParseWaveformBundle(root, diagnostics),
            "threshold-set" => ParseThresholdSet(root, diagnostics),
            "dependency-graph" => ParseDependencyGraph(root, diagnostics),
            "directives" => ParseDirectives(root, diagnostics),
            _ => null
        };
        return new ImportResult(kind, name, parent, payload, diagnostics);
    }

    private static WaveformBundle? ParseWaveformBundle(JsonElement root, List<string> diag)
    {
        if (!root.TryGetProperty("clock", out var clockEl))
        {
            diag.Add("格式错误: waveform-bundle 缺少 'clock'");
            return null;
        }
        var period = ParseNumber(clockEl, "period", "clock", diag);
        var clockUnit = TryString(clockEl, "unit") ?? "ms";
        var clockErr = ParseNumber(clockEl, "error", "clock", diag, defaultValue: 0);
        if (period is null || period <= 0)
        {
            diag.Add("格式错误: clock.period 必须为正数");
            return null;
        }
        var periodMs = Units.ToMilliseconds(period.Value, clockUnit);
        var clockErrMs = Units.ToMilliseconds(clockErr ?? 0, clockUnit);
        if (periodMs is null)
        {
            diag.Add($"诊断: 无法识别的时钟单位 '{clockUnit}'，时钟保留原始表示，相关派生量标记为未知");
            periodMs = double.NaN;
        }
        var clock = new ClockSpec(periodMs ?? double.NaN, clockUnit, clockErrMs ?? 0);

        var rails = new List<RailWaveform>();
        if (root.TryGetProperty("rails", out var railsEl))
        {
            foreach (var railEl in railsEl.EnumerateArray())
            {
                var railName = TryString(railEl, "name");
                if (railName is null) { diag.Add("格式错误: rail 缺少 'name'"); continue; }
                var unit = TryString(railEl, "unit") ?? "V";
                var errRaw = TryString(railEl, "error") ?? "0";
                if (!double.TryParse(errRaw, out var errVal))
                {
                    diag.Add($"诊断: rail {railName} 的 error='{errRaw}' 无法转换，按 0 处理，原值保留");
                    errVal = 0;
                }
                var samples = new List<Sample>();
                if (railEl.TryGetProperty("samples", out var samplesEl))
                {
                    var idx = 0;
                    foreach (var s in samplesEl.EnumerateArray())
                    {
                        var tRaw = TryString(s, "t") ?? "";
                        var vRaw = TryString(s, "v") ?? "";
                        var tOk = double.TryParse(tRaw, out var t);
                        var vOk = double.TryParse(vRaw, out var v);
                        if (!tOk) diag.Add($"诊断: {railName} 样本[{idx}] t='{tRaw}' 无法转换，保留在原位置");
                        if (!vOk) diag.Add($"诊断: {railName} 样本[{idx}] v='{vRaw}' 无法转换，保留在原位置");
                        var volts = vOk ? Units.ToVolts(new Quantity(vRaw, unit, v, errVal)) : null;
                        if (vOk && volts is null)
                            diag.Add($"诊断: {railName} 单位 '{unit}' 无法规范化为伏特，保留原始表示");
                        samples.Add(new Sample(tOk ? t : double.NaN,
                            new Quantity(vRaw, unit, volts ?? double.NaN, errVal)));
                        idx++;
                    }
                }
                rails.Add(new RailWaveform(railName, samples));
            }
        }
        if (rails.Count == 0) { diag.Add("格式错误: waveform-bundle 至少需要一个 rail"); return null; }

        var signals = new List<SignalWaveform>();
        if (root.TryGetProperty("signals", out var sigEl))
        {
            foreach (var s in sigEl.EnumerateArray())
            {
                var n = TryString(s, "name") ?? "?";
                var ss = new List<SignalSample>();
                if (s.TryGetProperty("samples", out var ssEl))
                    foreach (var x in ssEl.EnumerateArray())
                    {
                        var tRaw = TryString(x, "t") ?? "";
                        if (!double.TryParse(tRaw, out var t))
                            diag.Add($"诊断: 信号 {n} 样本 t='{tRaw}' 无法转换，保留在原位置");
                        ss.Add(new SignalSample(double.TryParse(tRaw, out var tt) ? tt : double.NaN,
                            TryString(x, "state") ?? "unknown"));
                    }
                signals.Add(new SignalWaveform(n, ss));
            }
        }
        return new WaveformBundle(clock, rails, signals);
    }

    private static ThresholdSet? ParseThresholdSet(JsonElement root, List<string> diag)
    {
        if (!root.TryGetProperty("rails", out var railsEl))
        {
            diag.Add("格式错误: threshold-set 缺少 'rails'");
            return null;
        }
        var rails = new List<RailThreshold>();
        foreach (var r in railsEl.EnumerateArray())
        {
            var rail = TryString(r, "rail");
            if (rail is null) { diag.Add("格式错误: threshold 缺少 'rail'"); continue; }
            var unit = TryString(r, "unit") ?? "V";
            var timeUnit = TryString(r, "timeUnit") ?? "ms";
            var rise = ParseQuantity(r, "rise", unit, rail, diag);
            var fall = ParseQuantity(r, "fall", unit, rail, diag);
            var hyst = ParseNumber(r, "hysteresis", rail, diag, defaultValue: 0) ?? 0;
            var minStable = ParseNumber(r, "minStable", rail, diag);
            if (rise is null || fall is null || minStable is null)
            {
                diag.Add($"格式错误: rail {rail} 的阈值不完整（rise/fall/minStable 为必需）");
                continue;
            }
            var minStableMs = Units.ToMilliseconds(minStable.Value, timeUnit);
            if (minStableMs is null)
            {
                diag.Add($"诊断: rail {rail} 时间单位 '{timeUnit}' 无法转换，保留原始表示");
                minStableMs = double.NaN;
            }
            if (rise.Value <= fall.Value)
                diag.Add($"诊断: rail {rail} 上升阈值 {rise.Value} 应大于下降阈值 {fall.Value}（滞回区间异常）");
            rails.Add(new RailThreshold(rail, rise, fall, hyst, minStableMs ?? double.NaN, timeUnit));
        }
        if (rails.Count == 0) { diag.Add("格式错误: threshold-set 没有有效 rail 阈值"); return null; }
        return new ThresholdSet(rails);
    }

    private static DependencyGraph? ParseDependencyGraph(JsonElement root, List<string> diag)
    {
        if (!root.TryGetProperty("edges", out var edgesEl))
        {
            diag.Add("格式错误: dependency-graph 缺少 'edges'");
            return null;
        }
        var edges = new List<DependencyEdge>();
        foreach (var e in edgesEl.EnumerateArray())
        {
            var before = TryString(e, "before");
            var after = TryString(e, "after");
            if (before is null || after is null)
            {
                diag.Add("格式错误: 依赖边缺少 'before'/'after'");
                continue;
            }
            edges.Add(new DependencyEdge(before, after, TryString(e, "scope") ?? "power-up"));
        }
        if (edges.Count == 0) { diag.Add("格式错误: dependency-graph 没有有效边"); return null; }
        return new DependencyGraph(edges);
    }

    private static DirectiveSet? ParseDirectives(JsonElement root, List<string> diag)
    {
        var steps = new List<DirectiveStep>();
        if (root.TryGetProperty("steps", out var stepsEl))
            foreach (var s in stepsEl.EnumerateArray())
                steps.Add(new DirectiveStep(s.GetRawText()));
        return new DirectiveSet(steps);
    }

    private static Quantity? ParseQuantity(JsonElement el, string prop, string unit, string rail, List<string> diag)
    {
        var raw = TryString(el, prop);
        if (raw is null) return null;
        if (!double.TryParse(raw, out var v))
        {
            diag.Add($"诊断: rail {rail} 的 {prop}='{raw}' 无法转换，保留原始表示");
            return new Quantity(raw, unit, double.NaN, 0);
        }
        var volts = Units.ToVolts(new Quantity(raw, unit, v, 0));
        if (volts is null)
            diag.Add($"诊断: rail {rail} 单位 '{unit}' 无法规范化，保留原始表示");
        return new Quantity(raw, unit, volts ?? double.NaN, 0);
    }

    private static double? ParseNumber(JsonElement el, string prop, string ctx, List<string> diag, double? defaultValue = null)
    {
        var raw = TryString(el, prop);
        if (raw is null) return defaultValue;
        if (double.TryParse(raw, out var v)) return v;
        diag.Add($"诊断: {ctx} 的 {prop}='{raw}' 无法转换，保留原始表示");
        return defaultValue;
    }

    private static string? TryString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Null => null,
            _ => v.GetRawText()
        };
    }
}

public sealed record DirectiveSet(IReadOnlyList<DirectiveStep> Steps);
public sealed record DirectiveStep(string RawJson);
