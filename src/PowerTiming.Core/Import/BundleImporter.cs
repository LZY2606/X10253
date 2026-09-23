using System.Text.Json;
using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Import;

/// <summary>
/// Accepts copy-paste sized domain samples. Imports fail with precise,
/// explicit format diagnostics rather than coercing unknown data away.
/// </summary>
public sealed class BundleImporter
{
    public ImportResult Import(string jsonText)
    {
        var diagnostics = new List<ImportDiagnostic>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(jsonText, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new ImportDiagnostic("E_JSON", "$", $"JSON syntax error: {ex.Message}", null));
            return new ImportResult(null, diagnostics);
        }

        using var owned = document;
        var root = owned.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ImportDiagnostic("E_SHAPE", "$", "Top-level bundle must be an object.", root.GetRawText()));
            return new ImportResult(null, diagnostics);
        }

        var schema = RequireString(root, "schema_version", diagnostics);
        if (schema != "power-timing-evidence-room/v1")
        {
            diagnostics.Add(new ImportDiagnostic("E_SCHEMA", "$.schema_version", "Expected 'power-timing-evidence-room/v1'.", schema));
        }

        var kindText = RequireString(root, "kind", diagnostics);
        if (!Enum.TryParse<BundleKind>(kindText, ignoreCase: true, out var kind))
        {
            diagnostics.Add(new ImportDiagnostic("E_ENUM", "$.kind", "Kind must be Baseline, ParameterRevision, SourcePatch, or Decision.", kindText));
        }

        var caseId = RequireString(root, "case_id", diagnostics);
        var title = RequireString(root, "title", diagnostics);
        var changeNote = OptionalString(root, "change_note") ?? string.Empty;
        var parentCommit = OptionalString(root, "parent_commit_id");
        var branch = OptionalString(root, "branch");

        if (kind != BundleKind.Baseline && string.IsNullOrWhiteSpace(parentCommit))
        {
            diagnostics.Add(new ImportDiagnostic("E_PARENT", "$.parent_commit_id", "Non-baseline submissions must name their parent commit.", null));
        }

        var isBaseline = kind == BundleKind.Baseline;
        ClockModel? clock = null;
        if (root.TryGetProperty("clock", out var clockElement))
        {
            clock = ParseClock(clockElement, diagnostics);
        }
        else if (isBaseline)
        {
            diagnostics.Add(new ImportDiagnostic("E_MISSING", "$.clock", "Baseline must preserve sampling clock, gaps, and measurement errors.", null));
        }

        var rails = ParseRails(root, diagnostics);
        var edges = ParseEdges(root, diagnostics);
        var decisions = ParseDecisions(root, diagnostics);

        var fingerprint = Hashing.Fingerprint(root.Clone());
        if (!isValidRailEdges(rails, edges, diagnostics))
        {
            return new ImportResult(null, diagnostics);
        }

        if (diagnostics.Any(d => d.Code.StartsWith("E_", StringComparison.Ordinal)))
        {
            return new ImportResult(null, diagnostics);
        }

        var bundle = new ImportBundle(
            schema,
            kind,
            caseId,
            title,
            parentCommit,
            branch,
            changeNote,
            clock,
            rails,
            edges,
            decisions,
            root.Clone(),
            jsonText,
            fingerprint);

        return new ImportResult(bundle, diagnostics);
    }

    private static bool isValidRailEdges(IReadOnlyDictionary<string, RailInput> rails, IReadOnlyDictionary<string, DependencyEdge> edges, List<ImportDiagnostic> diagnostics)
    {
        foreach (var edge in edges.Values)
        {
            if (!rails.ContainsKey(edge.FromRail) || !rails.ContainsKey(edge.ToRail))
            {
                diagnostics.Add(new ImportDiagnostic("E_DANGLING_EDGE", $"$.edges.{edge.EdgeId}", $"Edge endpoint must refer to a rail in this bundle or an inherited rail: {edge.FromRail}->{edge.ToRail}.", edge.EdgeId));
            }
        }

        return !diagnostics.Any(d => d.Code == "E_DANGLING_EDGE");
    }

    private static ClockModel ParseClock(JsonElement element, List<ImportDiagnostic> diagnostics)
    {
        var period = QuantityParser.Parse(element.TryGetProperty("nominal_period", out var p) ? p : (JsonElement?)null, "$.clock.nominal_period", "time", diagnostics);
        var start = QuantityParser.Parse(element.TryGetProperty("start", out var s) ? s : (JsonElement?)null, "$.clock.start", "time", diagnostics);
        var timeError = element.TryGetProperty("time_measurement_error", out var te) ? QuantityParser.Parse(te, "$.clock.time_measurement_error", "time", diagnostics, false) : null;
        var voltageError = element.TryGetProperty("voltage_measurement_error", out var ve) ? QuantityParser.Parse(ve, "$.clock.voltage_measurement_error", "voltage", diagnostics, false) : null;
        var gaps = new List<ClockGap>();
        if (element.TryGetProperty("gaps", out var gapArray) && gapArray.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var gap in gapArray.EnumerateArray())
            {
                var gStart = QuantityParser.Parse(gap.TryGetProperty("start", out var gs) ? gs : (JsonElement?)null, $"$.clock.gaps[{index}].start", "time", diagnostics);
                var gEnd = QuantityParser.Parse(gap.TryGetProperty("end", out var ge) ? ge : (JsonElement?)null, $"$.clock.gaps[{index}].end", "time", diagnostics);
                var reason = gap.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "clock gap" : "clock gap";
                gaps.Add(new ClockGap(gStart, gEnd, reason));
                index++;
            }
        }

        return new ClockModel(period, start, timeError, voltageError, gaps);
    }

    private static Dictionary<string, RailInput> ParseRails(JsonElement root, List<ImportDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, RailInput>(StringComparer.Ordinal);
        if (!root.TryGetProperty("rails", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new ImportDiagnostic("E_MISSING", "$.rails", "Bundle requires a rails array.", null));
            return result;
        }

        var railIndex = 0;
        foreach (var rail in array.EnumerateArray())
        {
            var basePath = $"$.rails[{railIndex}]";
            var railId = RequireString(rail, "rail_id", diagnostics, basePath);
            var name = OptionalString(rail, "name") ?? railId;
            var nominal = QuantityParser.Parse(rail.TryGetProperty("nominal", out var n) ? n : (JsonElement?)null, $"{basePath}.nominal", "voltage", diagnostics);
            var rise = QuantityParser.Parse(rail.TryGetProperty("rise_threshold", out var rt) ? rt : (JsonElement?)null, $"{basePath}.rise_threshold", "voltage", diagnostics);
            var fall = QuantityParser.Parse(rail.TryGetProperty("fall_threshold", out var ft) ? ft : (JsonElement?)null, $"{basePath}.fall_threshold", "voltage", diagnostics);
            var hysteresis = rail.TryGetProperty("hysteresis_band", out var hb) ? QuantityParser.Parse(hb, $"{basePath}.hysteresis_band", "voltage", diagnostics, false) : null;
            var stable = QuantityParser.Parse(rail.TryGetProperty("min_stable", out var ms) ? ms : (JsonElement?)null, $"{basePath}.min_stable", "time", diagnostics);

            var samples = new List<VoltageSample>();
            if (rail.TryGetProperty("samples", out var sampleArray) && sampleArray.ValueKind == JsonValueKind.Array)
            {
                var sampleIndex = 0;
                foreach (var sample in sampleArray.EnumerateArray())
                {
                    var samplePath = $"{basePath}.samples[{sampleIndex}]";
                    var at = QuantityParser.Parse(sample.TryGetProperty("t", out var t) ? t : (JsonElement?)null, $"{samplePath}.t", "time", diagnostics);
                    var v = QuantityParser.Parse(sample.TryGetProperty("v", out var vv) ? vv : (JsonElement?)null, $"{samplePath}.v", "voltage", diagnostics);
                    var error = sample.TryGetProperty("measurement_error", out var me) ? QuantityParser.Parse(me, $"{samplePath}.measurement_error", "voltage", diagnostics, false) : null;
                    var flags = new List<string>();
                    if (sample.TryGetProperty("flags", out var flagsElement) && flagsElement.ValueKind == JsonValueKind.Array)
                    {
                        flags.AddRange(flagsElement.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!));
                    }

                    var sampleId = Hashing.StableId("smpl", $"{railId}|{at.Raw}|{v.Raw}|{string.Join(",", flags)}", 12);
                    samples.Add(new VoltageSample(sampleIndex, at, v, error, flags, sampleId));
                    sampleIndex++;
                }
            }
            else
            {
                diagnostics.Add(new ImportDiagnostic("E_MISSING", $"{basePath}.samples", "Rail must preserve its raw sampled waveform.", null));
            }

            var signals = new List<SignalEdge>();
            if (rail.TryGetProperty("signals", out var signalArray) && signalArray.ValueKind == JsonValueKind.Array)
            {
                var signalIndex = 0;
                foreach (var signal in signalArray.EnumerateArray())
                {
                    var signalPath = $"{basePath}.signals[{signalIndex}]";
                    var signalName = RequireString(signal, "signal", diagnostics, signalPath);
                    var kind = RequireString(signal, "kind", diagnostics, signalPath);
                    if (kind is not ("enable_rise" or "enable_fall" or "good_rise" or "good_fall"))
                    {
                        diagnostics.Add(new ImportDiagnostic("E_ENUM", $"{signalPath}.kind", "Signal kind must be enable_rise, enable_fall, good_rise, or good_fall.", kind));
                    }

                    var at = QuantityParser.Parse(signal.TryGetProperty("at", out var sat) ? sat : (JsonElement?)null, $"{signalPath}.at", "time", diagnostics);
                    var source = OptionalString(signal, "source") ?? "captured-signal";
                    var signalId = Hashing.StableId("sig", $"{railId}|{signalName}|{kind}|{at.Raw}", 12);
                    signals.Add(new SignalEdge(signalName, kind, at, source, signalId));
                    signalIndex++;
                }
            }

            if (!result.TryAdd(railId, new RailInput(railId, name, nominal, rise, fall, hysteresis, stable, samples, signals)))
            {
                diagnostics.Add(new ImportDiagnostic("E_DUPLICATE", basePath, $"Duplicate rail_id '{railId}'.", railId));
            }

            railIndex++;
        }

        return result;
    }

    private static Dictionary<string, DependencyEdge> ParseEdges(JsonElement root, List<ImportDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, DependencyEdge>(StringComparer.Ordinal);
        if (!root.TryGetProperty("edges", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var edge in array.EnumerateArray())
        {
            var path = $"$.edges[{index}]";
            var edgeId = RequireString(edge, "edge_id", diagnostics, path);
            var from = RequireString(edge, "from_rail", diagnostics, path);
            var to = RequireString(edge, "to_rail", diagnostics, path);
            var minDelay = QuantityParser.Parse(edge.TryGetProperty("min_delay", out var md) ? md : (JsonElement?)null, $"{path}.min_delay", "time", diagnostics);
            var simultaneous = QuantityParser.Parse(edge.TryGetProperty("simultaneous_tolerance", out var st) ? st : (JsonElement?)null, $"{path}.simultaneous_tolerance", "time", diagnostics, false);
            var ruleVersion = OptionalString(edge, "rule_version") ?? "rules-1.0.0";
            var startup = ParseStringArray(edge, "startup_order", path, diagnostics);
            var shutdown = ParseStringArray(edge, "shutdown_order", path, diagnostics);
            if (!result.TryAdd(edgeId, new DependencyEdge(edgeId, from, to, minDelay, simultaneous, ruleVersion, startup, shutdown)))
            {
                diagnostics.Add(new ImportDiagnostic("E_DUPLICATE", path, $"Duplicate edge_id '{edgeId}'.", edgeId));
            }

            index++;
        }

        return result;
    }

    private static List<HumanDecision> ParseDecisions(JsonElement root, List<ImportDiagnostic> diagnostics)
    {
        var decisions = new List<HumanDecision>();
        if (!root.TryGetProperty("decisions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return decisions;
        }

        var index = 0;
        foreach (var decision in array.EnumerateArray())
        {
            var path = $"$.decisions[{index}]";
            var id = RequireString(decision, "decision_id", diagnostics, path);
            var candidateId = RequireString(decision, "candidate_id", diagnostics, path);
            var verdict = RequireString(decision, "verdict", diagnostics, path);
            if (verdict is not ("accept" or "reject" or "adjust"))
            {
                diagnostics.Add(new ImportDiagnostic("E_ENUM", $"{path}.verdict", "Verdict must be accept, reject, or adjust.", verdict));
            }

            var note = OptionalString(decision, "note") ?? string.Empty;
            var reviewer = OptionalString(decision, "reviewer") ?? "reviewer";
            var at = QuantityParser.Parse(decision.TryGetProperty("at", out var atElement) ? atElement : (JsonElement?)null, $"{path}.at", "time", diagnostics);
            decisions.Add(new HumanDecision(id, candidateId, verdict, note, reviewer, at));
            index++;
        }

        return decisions;
    }

    private static List<string> ParseStringArray(JsonElement parent, string property, string path, List<ImportDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new ImportDiagnostic("E_MISSING", $"{path}.{property}", "Order array is required.", null));
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new ImportDiagnostic("E_SHAPE", $"{path}.{property}", "Order entries must be rail id strings.", item.GetRawText()));
            }
            else
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    private static string RequireString(JsonElement parent, string name, List<ImportDiagnostic> diagnostics, string pathPrefix = "$")
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            return element.GetString()!;
        }

        diagnostics.Add(new ImportDiagnostic("E_MISSING", $"{pathPrefix}.{name}", "Required string field is missing or empty.", null));
        return string.Empty;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
