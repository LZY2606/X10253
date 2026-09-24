using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerSequencer.Domain;

/// <summary>A raw waveform sample. Raw representation is preserved exactly as imported.</summary>
public sealed record SamplePoint(double Time, double Value);

public sealed record RailWaveform(string Name, string Unit, IReadOnlyList<SamplePoint> Samples);

public sealed record SignalWaveform(string Name, IReadOnlyList<SamplePoint> Samples);

/// <summary>Imported multi-rail bundle: raw waveforms + sample clock + measurement error.</summary>
public sealed record WaveformBundle(
    string TimeUnit,
    double SampleClockPeriod,
    double MeasurementError,
    string ErrorUnit,
    IReadOnlyList<RailWaveform> Rails,
    IReadOnlyList<SignalWaveform> Signals);

public sealed record RailThreshold(
    string Rail,
    double RiseThreshold,
    double FallThreshold,
    double Hysteresis,
    double MinStableTime,
    string Unit,
    string TimeUnit);

public sealed record DependencyEdge(string From, string To, double MinDelay, string TimeUnit);

public sealed record ThresholdSet(
    string Name,
    IReadOnlyDictionary<string, RailThreshold> Rails,
    IReadOnlyList<DependencyEdge> Dependencies);

public sealed class ImportException(string message) : Exception(message);

public static class BundleJson
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    static double Req(JsonObject o, string key, string path)
    {
        if (o[key] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        throw new ImportException($"格式错误: {path}.{key} 缺失或不是数字");
    }

    static string ReqStr(JsonObject o, string key, string path)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        throw new ImportException($"格式错误: {path}.{key} 缺失或不是字符串");
    }

    static IReadOnlyList<SamplePoint> Samples(JsonNode? node, string path)
    {
        if (node is not JsonArray arr) throw new ImportException($"格式错误: {path} 缺失或不是数组");
        var list = new List<SamplePoint>();
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonArray pair || pair.Count != 2)
                throw new ImportException($"格式错误: {path}[{i}] 应为 [时间, 数值] 二元组");
            list.Add(new SamplePoint(
                pair[0]!.GetValue<double>(),
                pair[1]!.GetValue<double>()));
        }
        for (var i = 1; i < list.Count; i++)
            if (list[i].Time <= list[i - 1].Time)
                throw new ImportException($"格式错误: {path}[{i}] 采样时间必须严格递增");
        return list;
    }

    public static WaveformBundle ParseBundle(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new ImportException("格式错误: 顶层必须是 JSON 对象"); }
        catch (JsonException e) { throw new ImportException($"格式错误: JSON 解析失败 ({e.Message})"); }
        if (ReqStr(root, "kind", "$") != "waveform-bundle") throw new ImportException("格式错误: kind 必须为 waveform-bundle");
        var clock = root["sampleClock"] as JsonObject ?? throw new ImportException("格式错误: sampleClock 缺失");
        var period = Req(clock, "period", "sampleClock");
        if (period <= 0) throw new ImportException("格式错误: sampleClock.period 必须为正数");
        var timeUnit = ReqStr(clock, "unit", "sampleClock");
        var err = root["measurementError"] as JsonObject ?? throw new ImportException("格式错误: measurementError 缺失");
        var errVal = Req(err, "value", "measurementError");
        var errUnit = ReqStr(err, "unit", "measurementError");
        if (root["rails"] is not JsonArray rails || rails.Count == 0) throw new ImportException("格式错误: rails 缺失或为空");
        var railList = new List<RailWaveform>();
        for (var i = 0; i < rails.Count; i++)
        {
            var r = rails[i] as JsonObject ?? throw new ImportException($"格式错误: rails[{i}] 不是对象");
            railList.Add(new RailWaveform(ReqStr(r, "name", $"rails[{i}]"), ReqStr(r, "unit", $"rails[{i}]"), Samples(r["samples"], $"rails[{i}].samples")));
        }
        var sigList = new List<SignalWaveform>();
        if (root["signals"] is JsonArray sigs)
            for (var i = 0; i < sigs.Count; i++)
            {
                var s = sigs[i] as JsonObject ?? throw new ImportException($"格式错误: signals[{i}] 不是对象");
                sigList.Add(new SignalWaveform(ReqStr(s, "name", $"signals[{i}]"), Samples(s["samples"], $"signals[{i}].samples")));
            }
        return new WaveformBundle(timeUnit, period, errVal, errUnit, railList, sigList);
    }

    public static ThresholdSet ParseThresholds(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new ImportException("格式错误: 顶层必须是 JSON 对象"); }
        catch (JsonException e) { throw new ImportException($"格式错误: JSON 解析失败 ({e.Message})"); }
        if (ReqStr(root, "kind", "$") != "threshold-set") throw new ImportException("格式错误: kind 必须为 threshold-set");
        var name = ReqStr(root, "name", "$");
        var rails = new Dictionary<string, RailThreshold>();
        if (root["rails"] is not JsonObject railObj || railObj.Count == 0) throw new ImportException("格式错误: rails 缺失或为空");
        foreach (var (railName, node) in railObj)
        {
            var r = node as JsonObject ?? throw new ImportException($"格式错误: rails.{railName} 不是对象");
            var path = $"rails.{railName}";
            var th = new RailThreshold(railName, Req(r, "riseThreshold", path), Req(r, "fallThreshold", path),
                Req(r, "hysteresis", path), Req(r, "minStableTime", path), ReqStr(r, "unit", path), ReqStr(r, "timeUnit", path));
            if (th.FallThreshold >= th.RiseThreshold)
                throw new ImportException($"格式错误: {path} 下降阈值必须小于上升阈值(滞回)");
            rails[railName] = th;
        }
        var deps = new List<DependencyEdge>();
        if (root["dependencies"] is JsonArray depArr)
            for (var i = 0; i < depArr.Count; i++)
            {
                var d = depArr[i] as JsonObject ?? throw new ImportException($"格式错误: dependencies[{i}] 不是对象");
                deps.Add(new DependencyEdge(ReqStr(d, "from", $"dependencies[{i}]"), ReqStr(d, "to", $"dependencies[{i}]"),
                    Req(d, "minDelay", $"dependencies[{i}]"), ReqStr(d, "unit", $"dependencies[{i}]")));
            }
        return new ThresholdSet(name, rails, deps);
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Pretty);
}
