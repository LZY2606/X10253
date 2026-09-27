using System.Text.Json;
using System.Text.Json.Serialization;
using PowerSeq.Core.Model;

namespace PowerSeq.Core.Import;

/// <summary>阈值版本 JSON 解析。所有量保留原始文本（Qty.Parse），无法转换附诊断。</summary>
public static class ConstraintParser
{
    private sealed class RailDto
    {
        [JsonPropertyName("rail")] public string Rail { get; set; } = "";
        [JsonPropertyName("rise")] public string Rise { get; set; } = "";
        [JsonPropertyName("fall")] public string Fall { get; set; } = "";
        [JsonPropertyName("minStable")] public string MinStable { get; set; } = "";
        [JsonPropertyName("nominal")] public string Nominal { get; set; } = "";
        [JsonPropertyName("tolerance")] public string Tolerance { get; set; } = "";
    }

    private sealed class SignalDto
    {
        [JsonPropertyName("rail")] public string Rail { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "enable";
    }

    private sealed class EdgeDto
    {
        [JsonPropertyName("rail")] public string Rail { get; set; } = "";
        [JsonPropertyName("dependsOn")] public string DependsOn { get; set; } = "";
        [JsonPropertyName("minDelay")] public string MinDelay { get; set; } = "0";
        [JsonPropertyName("maxDelay")] public string MaxDelay { get; set; } = "";
        [JsonPropertyName("simulTolerance")] public string SimulTolerance { get; set; } = "0";
    }

    private sealed class DocDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("parentId")] public string? ParentId { get; set; }
        [JsonPropertyName("capture")] public string? Capture { get; set; }
        [JsonPropertyName("rails")] public List<RailDto> Rails { get; set; } = new();
        [JsonPropertyName("signals")] public List<SignalDto> Signals { get; set; } = new();
        [JsonPropertyName("edges")] public List<EdgeDto> Edges { get; set; } = new();
    }

    public static ConstraintDoc Parse(string json, int versionOverride = 0, string? idOverride = null, string? parentOverride = null)
    {
        DocDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DocDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ImportFormatException("约束 JSON 无法解析: " + ex.Message, 0);
        }
        if (dto is null) throw new ImportFormatException("约束 JSON 为空", 0);
        if (string.IsNullOrWhiteSpace(dto.Capture)) throw new ImportFormatException("约束缺少 capture", 0);

        var diags = new List<string>();
        var rails = new List<RailSpec>();
        foreach (var r in dto.Rails)
        {
            if (string.IsNullOrWhiteSpace(r.Rail)) throw new ImportFormatException("rail 条目缺少 rail 名", 0);
            var rise = Qty.Parse(r.Rise);
            var fall = Qty.Parse(r.Fall);
            var ms = Qty.Parse(r.MinStable);
            var nom = Qty.Parse(r.Nominal);
            var tol = Qty.Parse(r.Tolerance);
            foreach (var (name, q) in new[] { ("rise", rise), ("fall", fall), ("nominal", nom), ("tolerance", tol) })
                if (!q.IsConvertible) diags.Add($"轨 {r.Rail} 参数 {name}='{q.RawText}' 无法解析，保留原值");
            if (!ms.IsConvertible) diags.Add($"轨 {r.Rail} minStable='{ms.RawText}' 无法解析");
            if (rise.IsConvertible && fall.IsConvertible && rise.Value!.Value < fall.Value!.Value)
                diags.Add($"轨 {r.Rail} 上升阈值 {rise.RawText} 低于下降阈值 {fall.RawText}（需 rise≥fall 构成滞回），判定可能失效");
            rails.Add(new RailSpec(r.Rail, rise, fall, ms, nom, tol));
        }

        var signals = dto.Signals.Select(s => new SignalSpec(s.Rail,
            s.Kind.Equals("good", StringComparison.OrdinalIgnoreCase) ? ColumnKind.Good : ColumnKind.Enable)).ToList();

        var edges = new List<DepEdge>();
        foreach (var e in dto.Edges)
        {
            if (string.IsNullOrWhiteSpace(e.Rail) || string.IsNullOrWhiteSpace(e.DependsOn))
                throw new ImportFormatException("edge 必须同时给出 rail 与 dependsOn", 0);
            if (e.Rail == e.DependsOn)
                diags.Add($"自环边 {e.Rail}->{e.DependsOn} 本身构成循环依赖");
            edges.Add(new DepEdge(e.Rail, e.DependsOn, Qty.Parse(e.MinDelay), Qty.Parse(e.MaxDelay), Qty.Parse(e.SimulTolerance)));
        }

        var id = idOverride ?? dto.Id ?? $"constraint-{dto.Capture}-v{versionOverride}".ToLowerInvariant();
        var version = versionOverride > 0 ? versionOverride : dto.Version;
        return new ConstraintDoc
        {
            ConstraintId = id,
            Label = dto.Label ?? id,
            Version = version,
            ParentId = parentOverride ?? dto.ParentId,
            SourceCaptureId = dto.Capture,
            Rails = rails,
            Signals = signals,
            Edges = edges,
            Diagnostics = diags
        };
    }
}
