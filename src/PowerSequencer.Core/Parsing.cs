using System.Text.Json;

namespace PowerSequencer.Core;

public sealed class FormatException : Exception
{
    public FormatException(string message) : base(message) { }
}

/// <summary>Parsers for the import layer. Raw text is always preserved;
/// unconvertible values stay in place with diagnostics instead of being dropped.</summary>
public static class Parsing
{
    public static Waveform ParseWaveform(string text, string? parentId, int version)
    {
        string? rail = null, timeUnit = null, voltUnit = null, signal = "voltage";
        var points = new List<SamplePoint>();
        var diags = new List<ImportDiagnostic>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var body = line[1..];
                var colon = body.IndexOf(':');
                if (colon < 0) throw new FormatException($"第{lineNo}行: 头部应为 '# key: value'，实际: '{line}'");
                var key = body[..colon].Trim();
                var val = body[(colon + 1)..].Trim();
                switch (key)
                {
                    case "rail": rail = val; break;
                    case "time-unit": timeUnit = val; break;
                    case "voltage-unit": voltUnit = val; break;
                    case "signal":
                        if (val is not ("voltage" or "enable" or "good"))
                            throw new FormatException($"第{lineNo}行: signal 只能是 voltage/enable/good，实际: '{val}'");
                        signal = val; break;
                    default: throw new FormatException($"第{lineNo}行: 未知头部键 '{key}'（支持 rail/time-unit/voltage-unit/signal）");
                }
                continue;
            }
            if (rail is null)
                throw new FormatException($"第{lineNo}行: 数据行之前必须声明 '# rail: <名称>'");
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new FormatException($"第{lineNo}行: 数据行应为 '<时间> <电压>' 两列，实际: '{line}'");
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t))
                throw new FormatException($"第{lineNo}行: 时间 '{parts[0]}' 不是数字");
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"第{lineNo}行: 电压 '{parts[1]}' 不是数字");
            points.Add(new SamplePoint(t, v, parts[0], parts[1]));
        }
        if (rail is null) throw new FormatException("缺少 '# rail: <名称>' 头部");
        timeUnit ??= "us";
        voltUnit ??= "V";
        if (Units.ToUs(1, timeUnit) is null)
            diags.Add(new ImportDiagnostic(null, $"未知时间单位 '{timeUnit}'，无法转换为 us；原始值保留", timeUnit));
        if (Units.ToV(1, voltUnit) is null)
            diags.Add(new ImportDiagnostic(null, $"未知电压单位 '{voltUnit}'，无法转换为 V；原始值保留", voltUnit));
        if (points.Count == 0) throw new FormatException("没有任何数据行");
        var ordered = points.OrderBy(p => p.Time).ToList();
        var wf = new Waveform("", parentId, version, rail, signal!, timeUnit, voltUnit, ordered, diags, text);
        return wf with { Id = Ids.Of("wf", Ids.Canonical(wf with { Id = "" })) };
    }

    public static ThresholdSet ParseThresholds(string text, string? parentId, int version)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new FormatException($"阈值 JSON 解析失败: {ex.Message}"); }
        if (!doc.RootElement.TryGetProperty("rails", out var railsEl) || railsEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("阈值 JSON 必须包含对象属性 'rails'");
        var rails = new Dictionary<string, RailThreshold>();
        foreach (var prop in railsEl.EnumerateObject())
        {
            var r = prop.Value;
            string[] required = { "rise", "fall", "hysteresis", "minStableUs" };
            foreach (var k in required)
                if (!r.TryGetProperty(k, out _) || r.GetProperty(k).ValueKind != JsonValueKind.Number)
                    throw new FormatException($"轨 '{prop.Name}' 缺少数值字段 '{k}'");
            var rt = new RailThreshold(prop.Name,
                r.GetProperty("rise").GetDouble(), r.GetProperty("fall").GetDouble(),
                r.GetProperty("hysteresis").GetDouble(), r.GetProperty("minStableUs").GetDouble());
            if (rt.FallV >= rt.RiseV)
                throw new FormatException($"轨 '{prop.Name}': 下降阈值 {rt.FallV} 必须小于上升阈值 {rt.RiseV}（滞回要求）");
            if (rt.MinStableUs <= 0)
                throw new FormatException($"轨 '{prop.Name}': minStableUs 必须为正");
            rails[prop.Name] = rt;
        }
        if (rails.Count == 0) throw new FormatException("'rails' 为空");
        var ts = new ThresholdSet("", parentId, version, rails, text);
        return ts with { Id = Ids.Of("th", Ids.Canonical(ts with { Id = "" })) };
    }

    public static DependencyGraphInput ParseDependencies(string text, string? parentId, int version)
    {
        var edges = new List<DepEdge>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split("->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new FormatException($"第{lineNo}行: 依赖应为 '<先决轨> -> <后续轨>'，实际: '{line}'");
            edges.Add(new DepEdge(parts[0], parts[1]));
        }
        if (edges.Count == 0) throw new FormatException("没有任何依赖边");
        var dg = new DependencyGraphInput("", parentId, version, edges, text);
        return dg with { Id = Ids.Of("dep", Ids.Canonical(dg with { Id = "" })) };
    }

    public static ClockSpec ParseClock(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new FormatException($"时钟 JSON 解析失败: {ex.Message}"); }
        if (!doc.RootElement.TryGetProperty("nominalPeriodUs", out var p) || p.ValueKind != JsonValueKind.Number)
            throw new FormatException("时钟 JSON 缺少数值字段 'nominalPeriodUs'");
        double factor = doc.RootElement.TryGetProperty("maxGapFactor", out var f) && f.ValueKind == JsonValueKind.Number
            ? f.GetDouble() : 2.5;
        if (p.GetDouble() <= 0) throw new FormatException("nominalPeriodUs 必须为正");
        var cs = new ClockSpec("", p.GetDouble(), factor, text);
        return cs with { Id = Ids.Of("clk", Ids.Canonical(cs with { Id = "" })) };
    }

    public static ErrorSpec ParseError(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new FormatException($"测量误差 JSON 解析失败: {ex.Message}"); }
        if (!doc.RootElement.TryGetProperty("voltageTolV", out var v) || v.ValueKind != JsonValueKind.Number)
            throw new FormatException("测量误差 JSON 缺少数值字段 'voltageTolV'");
        if (!doc.RootElement.TryGetProperty("timeTolUs", out var t) || t.ValueKind != JsonValueKind.Number)
            throw new FormatException("测量误差 JSON 缺少数值字段 'timeTolUs'");
        var es = new ErrorSpec("", v.GetDouble(), t.GetDouble(), text);
        return es with { Id = Ids.Of("err", Ids.Canonical(es with { Id = "" })) };
    }
}
