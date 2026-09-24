using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerSeq.Domain;

/// <summary>单条轨压采样序列。T/V 已按派生层规范化（ms / V），RawUnit 保留导入时的原始单位。</summary>
public sealed record RailSamples(
    string Name,
    string RawUnit,
    IReadOnlyList<(double TMs, double V)> Samples,
    double SamplePeriodMs,
    Quantity MeasError,
    IReadOnlyList<string> Diagnostics);

/// <summary>enable/good 数字信号。</summary>
public sealed record SignalSamples(string Name, IReadOnlyList<(double TMs, double Level)> Samples);

public sealed record WaveformSet(
    string Version,
    string? ParentVersion,
    IReadOnlyList<RailSamples> Rails,
    IReadOnlyList<SignalSamples> Signals,
    IReadOnlyList<string> Diagnostics,
    string RawJson);

public sealed record RailThreshold(Quantity Rise, Quantity Fall, Quantity MinStable)
{
    public double RiseV => Rise.CanonicalOrThrow(Quantity.VoltageCanonical);
    public double FallV => Fall.CanonicalOrThrow(Quantity.VoltageCanonical);
    public double MinStableMs => MinStable.CanonicalOrThrow(Quantity.TimeCanonical);
}

public sealed record ThresholdSet(
    string Version,
    string? ParentVersion,
    IReadOnlyDictionary<string, RailThreshold> Rails,
    string RawJson);

public sealed record DepEdge(string Before, string After, Quantity? MinGap)
{
    public string Key => $"{Before}->{After}";
}

public sealed record DepGraph(
    string Version,
    string? ParentVersion,
    IReadOnlyList<DepEdge> Edges,
    string RawJson);

public static class InputParser
{
    /// <summary>识别导入文本属于哪类源。</summary>
    public static string DetectKind(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var fmt = doc.RootElement.GetProperty("format").GetString() ?? "";
        if (fmt.StartsWith("powerseq-waveforms/")) return "waveforms";
        if (fmt.StartsWith("powerseq-thresholds/")) return "thresholds";
        if (fmt.StartsWith("powerseq-deps/")) return "deps";
        throw new FormatException($"无法识别的 format: '{fmt}'（需要 powerseq-waveforms/1、powerseq-thresholds/1 或 powerseq-deps/1）");
    }

    public static WaveformSet ParseWaveforms(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var diagnostics = new List<string>();
        var version = node["version"]!.GetValue<string>();
        var parent = node["parent"]?.GetValue<string>();
        var clockNode = node["sampleClock"] ?? throw new FormatException("waveforms 缺少 sampleClock");
        var clockRaw = clockNode!["raw"]!.GetValue<string>();
        if (!Quantity.TryParse(clockRaw, out var clockQ, out var clockDiag))
            throw new FormatException($"sampleClock 解析失败: {clockDiag}");
        var periodMs = clockQ.ToCanonical(Quantity.TimeCanonical, out var clockConvDiag)
            ?? throw new FormatException($"sampleClock 无法换算: {clockConvDiag}");

        var rails = new List<RailSamples>();
        foreach (var railNode in node["rails"]!.AsArray())
        {
            var r = railNode!.AsObject();
            var name = r["name"]!.GetValue<string>();
            var rawUnit = r["unit"]!.GetValue<string>();
            var railDiags = new List<string>();
            var errRaw = r["measurementError"]?["raw"]?.GetValue<string>() ?? "0 V";
            Quantity.TryParse(errRaw, out var errQ, out var errDiag);
            if (errDiag != null) railDiags.Add(errDiag);
            var samples = new List<(double, double)>();
            var unitOk = Quantity.Table0.ContainsKey(rawUnit);
            if (!unitOk)
                railDiags.Add($"轨 {name}: 未知单位 '{rawUnit}'，采样值保留原始数值但不参与派生计算");
            foreach (var pair in r["samples"]!.AsArray())
            {
                var t = pair![0]!.GetValue<double>();
                var v = pair[1]!.GetValue<double>();
                samples.Add((t, unitOk ? v * Quantity.Table0[rawUnit].Factor : v));
            }
            rails.Add(new RailSamples(name, rawUnit, samples, periodMs, errQ, railDiags));
        }
        var signals = new List<SignalSamples>();
        foreach (var sigNode in node["signals"]?.AsArray() ?? new JsonArray())
        {
            var s = sigNode!.AsObject();
            var pts = s["samples"]!.AsArray()
                .Select(p => (p![0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToList();
            signals.Add(new SignalSamples(s["name"]!.GetValue<string>(), pts));
        }
        diagnostics.AddRange(rails.SelectMany(r => r.Diagnostics));
        return new WaveformSet(version, parent, rails, signals, diagnostics, json);
    }

    public static ThresholdSet ParseThresholds(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var version = node["version"]!.GetValue<string>();
        var parent = node["parent"]?.GetValue<string>();
        var rails = new Dictionary<string, RailThreshold>();
        foreach (var kv in node["rails"]!.AsObject())
        {
            var r = kv.Value!.AsObject();
            Quantity Parse(string field)
            {
                var raw = r[field]!["raw"]!.GetValue<string>();
                if (!Quantity.TryParse(raw, out var q, out var diag))
                    throw new FormatException($"阈值 {kv.Key}.{field} 解析失败: {diag}");
                return q;
            }
            rails[kv.Key] = new RailThreshold(Parse("rise"), Parse("fall"), Parse("minStable"));
        }
        return new ThresholdSet(version, parent, rails, json);
    }

    public static DepGraph ParseDeps(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var version = node["version"]!.GetValue<string>();
        var parent = node["parent"]?.GetValue<string>();
        var edges = new List<DepEdge>();
        foreach (var e in node["edges"]!.AsArray())
        {
            var eo = e!.AsObject();
            Quantity? minGap = null;
            var gapRaw = eo["minGap"]?["raw"]?.GetValue<string>();
            if (gapRaw != null)
            {
                if (!Quantity.TryParse(gapRaw, out var q, out var diag))
                    throw new FormatException($"依赖边 minGap 解析失败: {diag}");
                minGap = q;
            }
            edges.Add(new DepEdge(eo["before"]!.GetValue<string>(), eo["after"]!.GetValue<string>(), minGap));
        }
        return new DepGraph(version, parent, edges, json);
    }
}
