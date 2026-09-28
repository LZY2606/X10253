using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerEvidenceRoom.Domain;

public static class PackageImporter
{
    public static ImportedPackage Import(string text, string? parentPackageId = null)
    {
        JsonObject root;
        var formatErrors = new List<Diagnostic>();
        try
        {
            root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new ImportFormatException(new[]
            {
                new Diagnostic("error", "INVALID_JSON", "导入包必须是可解析的 JSON 对象。", null, "$")
            });
        }

        RequireString(root, "schemaVersion", "$.schemaVersion", formatErrors);
        RequireString(root, "projectId", "$.projectId", formatErrors);
        RequireString(root, "name", "$.name", formatErrors);
        RequireArray(root, "rails", "$.rails", formatErrors);
        RequireArray(root, "waveforms", "$.waveforms", formatErrors);
        RequireArray(root, "signals", "$.signals", formatErrors);
        RequireArray(root, "thresholdSets", "$.thresholdSets", formatErrors);
        RequireArray(root, "constraintGraphs", "$.constraintGraphs", formatErrors);
        RequireObject(root, "clock", "$.clock", formatErrors);
        RequireObject(root, "measurement", "$.measurement", formatErrors);

        if (formatErrors.Count > 0)
        {
            throw new ImportFormatException(formatErrors);
        }

        var original = root.DeepClone().AsObject();
        var current = root.DeepClone().AsObject();
        var fingerprint = Identity.Fingerprint(original);
        var sources = new List<SourceDocument>();
        var diagnostics = new List<Diagnostic>();

        AddSource(sources, diagnostics, root, SourceKind.Package, root["projectId"]!.GetValue<string>(),
            original, 1, null);
        AddNamedSection(sources, diagnostics, root, SourceKind.RailCatalog, "rails", "rails");
        AddNamedSection(sources, diagnostics, root, SourceKind.Waveform, "waveforms", "waveforms");
        AddNamedSection(sources, diagnostics, root, SourceKind.Signal, "signals", "signals");
        AddNamedSection(sources, diagnostics, root, SourceKind.ThresholdSet, "thresholdSets", "thresholdSets");
        AddNamedSection(sources, diagnostics, root, SourceKind.ConstraintGraph, "constraintGraphs", "constraintGraphs");
        AddNamedSection(sources, diagnostics, root, SourceKind.SamplingClock, "clock", "clock");
        AddNamedSection(sources, diagnostics, root, SourceKind.MeasurementModel, "measurement", "measurement");

        ValidateDomain(root, diagnostics);

        return new ImportedPackage(
            original,
            current,
            fingerprint,
            parentPackageId,
            sources,
            diagnostics);
    }

    private static void AddNamedSection(
        List<SourceDocument> sources,
        List<Diagnostic> diagnostics,
        JsonObject root,
        SourceKind kind,
        string section,
        string subject)
    {
        if (root[section] is JsonNode node)
        {
            var sectionDiagnostics = new List<Diagnostic>();
            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i]?.AsObject();
                    if (item is not null && item["id"]?.GetValue<string>() is { } id)
                    {
                        AddSource(sources, sectionDiagnostics, root, kind, id, item, 1, null);
                    }
                    else
                    {
                        sectionDiagnostics.Add(new Diagnostic("error", "MISSING_ID",
                            $"{section}[{i}] 缺少稳定 id。", null, $"$.{section}[{i}].id"));
                    }
                }
            }
            else
            {
                AddSource(sources, sectionDiagnostics, root, kind, subject, node, 1, null);
            }
            diagnostics.AddRange(sectionDiagnostics);
        }
    }

    private static void AddSource(
        List<SourceDocument> sources,
        List<Diagnostic> diagnostics,
        JsonObject root,
        SourceKind kind,
        string subject,
        JsonNode original,
        int version,
        int? parentVersion)
    {
        var fp = Identity.Fingerprint(original);
        var id = $"src:{kind.ToString().ToLowerInvariant()}:{subject}:v{version}:{Identity.Short(fp)}";
        sources.Add(new SourceDocument(id, kind, subject, version, parentVersion, fp, original.DeepClone(), Array.Empty<Diagnostic>()));
        if (kind == SourceKind.Waveform)
        {
            InspectWaveform(original, id, diagnostics);
        }
    }

    private static void InspectWaveform(JsonNode waveform, string sourceId, List<Diagnostic> diagnostics)
    {
        if (waveform["samples"] is not JsonArray samples)
        {
            return;
        }

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var rawValue = sample?["value"]?.ToJsonString();
            var rawUnit = sample?["unit"]?.GetValue<string>();
            if (!QuantityParser.TryNormalizeVolts(rawValue, rawUnit, out _, out var reason))
            {
                diagnostics.Add(new Diagnostic("warning", reason,
                    $"样本保留在 {sourceId}，无法规范化为伏特。",
                    sourceId, $"samples[{i}].value"));
            }
        }
    }

    private static void ValidateDomain(JsonObject root, List<Diagnostic> diagnostics)
    {
        var railIds = new HashSet<string>(((JsonArray)root["rails"]!).Select(x => x!["id"]!.GetValue<string>()));
        ValidateSubjects((JsonArray)root["waveforms"]!, railIds, "waveforms", diagnostics);
        ValidateSubjects((JsonArray)root["signals"]!, railIds, "signals", diagnostics);
        ValidateSubjects((JsonArray)root["thresholdSets"]!, railIds, "thresholdSets", diagnostics, "railId");

        foreach (var graph in (JsonArray)root["constraintGraphs"]!)
        {
            var edges = graph!["edges"]?.AsArray();
            if (edges is null)
            {
                diagnostics.Add(new Diagnostic("error", "MISSING_EDGES", "依赖图必须包含 edges。",
                    null, $"$.constraintGraphs[{graph.GetPath()}]"));
                continue;
            }

            foreach (var edge in edges)
            {
                var from = edge?["from"]?.GetValue<string>();
                var to = edge?["to"]?.GetValue<string>();
                if (from is null || !railIds.Contains(from) || to is null || !railIds.Contains(to))
                {
                    diagnostics.Add(new Diagnostic("error", "UNKNOWN_RAIL_EDGE",
                        $"依赖边 {from}->{to} 引用了未知轨。", null, edge?.GetPath()));
                }
                if (edge?["id"]?.GetValue<string>() is null)
                {
                    diagnostics.Add(new Diagnostic("error", "MISSING_EDGE_ID",
                        "依赖边必须携带稳定 id。", null, $"{edge?.GetPath()}.id"));
                }
            }
        }
    }

    private static void ValidateSubjects(
        JsonArray array,
        HashSet<string> railIds,
        string section,
        List<Diagnostic> diagnostics,
        string field = "railId")
    {
        foreach (var item in array)
        {
            var railId = item?[field]?.GetValue<string>();
            if (railId is null || !railIds.Contains(railId))
            {
                diagnostics.Add(new Diagnostic("error", "UNKNOWN_RAIL",
                    $"{section} 条目必须引用已知轨。", null, item?.GetPath()));
            }
        }
    }

    private static void RequireString(JsonObject root, string field, string coordinate, List<Diagnostic> errors)
    {
        if (root[field]?.GetValue<string>() is not { Length: > 0 })
        {
            errors.Add(new Diagnostic("error", "MISSING_REQUIRED", $"缺少必填字符串 {field}。", null, coordinate));
        }
    }

    private static void RequireArray(JsonObject root, string field, string coordinate, List<Diagnostic> errors)
    {
        if (root[field] is not JsonArray)
        {
            errors.Add(new Diagnostic("error", "MISSING_ARRAY", $"缺少必填数组 {field}。", null, coordinate));
        }
    }

    private static void RequireObject(JsonObject root, string field, string coordinate, List<Diagnostic> errors)
    {
        if (root[field] is not JsonObject)
        {
            errors.Add(new Diagnostic("error", "MISSING_OBJECT", $"缺少必填对象 {field}。", null, coordinate));
        }
    }
}

public static class QuantityParser
{
    public static bool TryNormalizeVolts(string? raw, string? unit, out double volts, out string reason)
    {
        volts = 0;
        reason = "UNCONVERTIBLE_QUANTITY";
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(unit))
        {
            reason = "MISSING_QUANTITY";
            return false;
        }

        if (!double.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        switch (unit.Trim().ToLowerInvariant())
        {
            case "v":
                volts = value;
                return true;
            case "mv":
                volts = value / 1000d;
                return true;
            case "uv":
                volts = value / 1_000_000d;
                return true;
            default:
                reason = "UNKNOWN_VOLTAGE_UNIT";
                return false;
        }
    }

    public static bool TryParseNs(JsonNode? node, out long value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }
        if (node.GetValueKind() == JsonValueKind.Number)
        {
            return long.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        var raw = node.GetValue<string>();
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var number))
        {
            return false;
        }
        if (!parts[1].Equals("ns", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        value = number;
        return true;
    }
}
