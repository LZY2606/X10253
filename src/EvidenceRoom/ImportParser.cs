using System.Text.Json;

namespace EvidenceRoom;

/// <summary>
/// Import layer: accepts copyable small JSON samples, reports explicit format errors.
/// Raw representation is preserved; unconvertible values stay in place with diagnostics.
/// </summary>
public static class ImportParser
{
    public sealed record ParsedImport(string Kind, string Label, string PayloadJson, List<string> Diagnostics);

    public sealed class FormatException(string message, List<string> errors) : Exception(message)
    {
        public List<string> Errors { get; } = errors;
    }

    public static ParsedImport Parse(string text)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException je) { throw new FormatException("JSON 语法错误", new() { $"JSON 语法错误: {je.Message}" }); }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("顶层必须是对象", new() { "顶层必须是 JSON 对象" });

        var kind = ReqString(root, "kind", errors);
        var label = OptString(root, "label") ?? "未命名";
        if (errors.Count > 0) throw new FormatException("格式错误", errors);

        return kind switch
        {
            "waveform" => ParseWaveform(root, label, errors),
            "thresholds" => ParseThresholds(root, label, errors),
            "dependencies" => ParseDependencies(root, label, errors),
            _ => throw new FormatException("未知 kind",
                new() { $"kind 必须是 waveform|thresholds|dependencies，实际为 '{kind}'" })
        };
    }

    private static ParsedImport ParseWaveform(JsonElement root, string label, List<string> errors)
    {
        var diags = new List<string>();
        var timeBasis = OptString(root, "timeBasis") ?? "unspecified";
        if (!root.TryGetProperty("clock", out var clock) || !clock.TryGetProperty("interval", out var ci))
            errors.Add("waveform 缺少 clock.interval（采样时钟，必填）");
        if (!root.TryGetProperty("error", out var errEl) || !errEl.TryGetProperty("voltage", out var ve))
            errors.Add("waveform 缺少 error.voltage（测量误差，必填）");
        if (!root.TryGetProperty("rails", out var rails) || rails.ValueKind != JsonValueKind.Array)
            errors.Add("waveform 缺少 rails 数组");
        if (errors.Count > 0) throw new FormatException("格式错误", errors);

        var clock = Quantity.Parse(ci.GetString() ?? "", "time");
        var voltErr = Quantity.Parse(ve.GetString() ?? "", "voltage");
        if (clock.Diagnostic is not null) diags.Add($"clock.interval '{clock.Raw}': {clock.Diagnostic}");
        if (voltErr.Diagnostic is not null) diags.Add($"error.voltage '{voltErr.Raw}': {voltErr.Diagnostic}");

        var railList = new List<RailWaveform>();
        var ri = 0;
        foreach (var r in rails.EnumerateArray())
        {
            var where = $"rails[{ri}]";
            var name = ReqString(r, "name", errors, where);
            var unit = OptString(r, "unit") ?? "V";
            if (!r.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
                errors.Add($"{where}: 缺少 samples 数组");
            var pts = new List<SamplePoint>();
            if (samples.ValueKind == JsonValueKind.Array)
            {
                var si = 0;
                foreach (var s in samples.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() != 2)
                    { errors.Add($"{where}.samples[{si}]: 必须是 [时间, 电压] 二元组"); si++; continue; }
                    var tq = Quantity.Parse(s[0].GetString() ?? "", "time");
                    var vq = Quantity.Parse(s[1].GetString() ?? "", "voltage");
                    pts.Add(new SamplePoint(tq, vq));
                    si++;
                }
            }
            railList.Add(new RailWaveform(name ?? $"rail{ri}", unit, pts,
                ParseLevels(r, "enable", errors, where), ParseLevels(r, "good", errors, where)));
            ri++;
        }
        if (errors.Count > 0) throw new FormatException("格式错误", errors);
        var payload = new WaveformPayload(timeBasis, clock, voltErr, railList);
        return new ParsedImport("waveform", label, SerPayload(payload), diags);
    }

    private static List<(Quantity, string)> ParseLevels(JsonElement r, string prop, List<string> errors, string where)
    {
        var list = new List<(Quantity, string)>();
        if (!r.TryGetProperty(prop, out var el)) return list;
        if (el.ValueKind != JsonValueKind.Array) { errors.Add($"{where}.{prop}: 必须是 [时间, 电平] 数组"); return list; }
        var i = 0;
        foreach (var s in el.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() != 2)
            { errors.Add($"{where}.{prop}[{i}]: 必须是 [时间, 电平] 二元组"); i++; continue; }
            var level = (s[1].GetString() ?? "").ToLowerInvariant();
            if (level is not ("low" or "high"))
                errors.Add($"{where}.{prop}[{i}]: 电平必须是 low|high，实际为 '{level}'");
            list.Add((Quantity.Parse(s[0].GetString() ?? "", "time"), level));
            i++;
        }
        return list;
    }

    private static ParsedImport ParseThresholds(JsonElement root, string label, List<string> errors)
    {
        var diags = new List<string>();
        if (!root.TryGetProperty("rails", out var rails) || rails.ValueKind != JsonValueKind.Object)
            throw new FormatException("格式错误", new() { "thresholds 缺少 rails 对象" });
        var map = new Dictionary<string, RailThreshold>();
        foreach (var rp in rails.EnumerateObject())
        {
            foreach (var f in new[] { "rise", "fall", "minStable" })
                if (!rp.Value.TryGetProperty(f, out _))
                    errors.Add($"thresholds.rails.{rp.Name}: 缺少 {f}");
            if (errors.Count > 0) continue;
            var rise = Quantity.Parse(rp.Value.GetProperty("rise").GetString() ?? "", "voltage");
            var fall = Quantity.Parse(rp.Value.GetProperty("fall").GetString() ?? "", "voltage");
            var ms = Quantity.Parse(rp.Value.GetProperty("minStable").GetString() ?? "", "time");
            foreach (var q in new[] { rise, fall, ms })
                if (q.Diagnostic is not null) diags.Add($"{rp.Name}: '{q.Raw}': {q.Diagnostic}");
            if (rise.Normalized is not null && fall.Normalized is not null &&
                rise.Normalized <= fall.Normalized)
                errors.Add($"thresholds.rails.{rp.Name}: 上升阈值 {rise.Raw} 必须大于下降阈值 {fall.Raw}（滞回区间为空）");
            map[rp.Name] = new RailThreshold(rise, fall, ms);
        }
        if (errors.Count > 0) throw new FormatException("格式错误", errors);
        return new ParsedImport("thresholds", label, SerPayload(new ThresholdPayload(map)), diags);
    }

    private static ParsedImport ParseDependencies(JsonElement root, string label, List<string> errors)
    {
        var diags = new List<string>();
        if (!root.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            throw new FormatException("格式错误", new() { "dependencies 缺少 edges 数组" });
        var list = new List<DependencyEdge>();
        var i = 0;
        foreach (var e in edges.EnumerateArray())
        {
            var where = $"edges[{i}]";
            var from = ReqString(e, "from", errors, where);
            var to = ReqString(e, "to", errors, where);
            var phase = OptString(e, "phase") ?? "both";
            if (phase is not ("startup" or "shutdown" or "both"))
                errors.Add($"{where}.phase: 必须是 startup|shutdown|both，实际为 '{phase}'");
            if (!e.TryGetProperty("minDelay", out var md))
                errors.Add($"{where}: 缺少 minDelay");
            var delay = Quantity.Parse(md.ValueKind == JsonValueKind.String ? md.GetString()! : "0s", "time");
            if (delay.Diagnostic is not null) diags.Add($"{where}.minDelay '{delay.Raw}': {delay.Diagnostic}");
            if (from is not null && to is not null)
                list.Add(new DependencyEdge(from, to, delay, phase));
            i++;
        }
        if (errors.Count > 0) throw new FormatException("格式错误", errors);
        return new ParsedImport("dependencies", label, SerPayload(new DependencyPayload(list)), diags);
    }

    private static string SerPayload<T>(T payload) => J.Ser(J.Elem(payload));

    private static string? ReqString(JsonElement el, string prop, List<string> errors, string? where = null)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String)
        { errors.Add($"{(where is null ? "" : where + ".")}{prop}: 缺少必填字符串字段"); return null; }
        return v.GetString();
    }

    private static string? OptString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
