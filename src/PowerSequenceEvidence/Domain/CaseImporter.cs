using System.Text.Json;

namespace PowerSequenceEvidence.Domain;

public static class CaseImporter
{
    public static EvidenceCase ImportEnvelope(JsonElement root)
    {
        var diagnostics = new List<InputDiagnostic>();
        var waveforms = new Dictionary<string, RailWaveform>(StringComparer.Ordinal);
        var signals = new Dictionary<string, SignalTrace>(StringComparer.Ordinal);
        var thresholds = new Dictionary<string, ThresholdSet>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("案例包必须是 JSON 对象。");

        foreach (var waveformElement in RequireArray(root, "rails", diagnostics))
        {
            var railId = RequireString(waveformElement, "id", "rail", diagnostics);
            if (railId is null) continue;
            var samples = new List<RailSample>();
            var index = 0;
            foreach (var sampleElement in RequireArray(waveformElement, "samples", diagnostics))
            {
                var timeRaw = RequireString(sampleElement, "t", $"rail:{railId}:sample:{index}", diagnostics) ?? "";
                var voltageRaw = RequireString(sampleElement, "v", $"rail:{railId}:sample:{index}", diagnostics) ?? "";
                var time = PhysicalQuantity.Parse(timeRaw, "s", ("ms", 0.001), ("us", 0.000001), ("s", 1));
                var voltage = PhysicalQuantity.Parse(voltageRaw, "V", ("mV", 0.001), ("V", 1));
                if (!time.Convertible)
                    diagnostics.Add(new InputDiagnostic("error", "TIME_NOT_CONVERTIBLE", $"rail:{railId}:sample:{index}:t", "时间值无法规范化，已保留原位置。", timeRaw));
                if (!voltage.Convertible)
                    diagnostics.Add(new InputDiagnostic("error", "VOLTAGE_NOT_CONVERTIBLE", $"rail:{railId}:sample:{index}:v", "电压值无法规范化，已保留原位置。", voltageRaw));
                samples.Add(new RailSample(index, timeRaw, time.Value, time.Convertible, voltageRaw, voltage.Value, voltage.Convertible));
                index++;
            }
            waveforms[railId] = new RailWaveform(railId, "s", samples);
        }

        foreach (var signalElement in RequireArray(root, "signals", diagnostics))
        {
            var signalId = RequireString(signalElement, "id", "signal", diagnostics);
            var railId = RequireString(signalElement, "rail", $"signal:{signalId}", diagnostics);
            var role = RequireString(signalElement, "role", $"signal:{signalId}", diagnostics) ?? "enable";
            if (signalId is null || railId is null) continue;
            var points = new List<SignalPoint>();
            var index = 0;
            foreach (var pointElement in RequireArray(signalElement, "points", diagnostics))
            {
                var timeRaw = RequireString(pointElement, "t", $"signal:{signalId}:{index}", diagnostics) ?? "";
                var rawValue = RequireString(pointElement, "v", $"signal:{signalId}:{index}", diagnostics) ?? "";
                var time = PhysicalQuantity.Parse(timeRaw, "s", ("ms", 0.001), ("us", 0.000001), ("s", 1));
                if (!time.Convertible)
                    diagnostics.Add(new InputDiagnostic("error", "SIGNAL_TIME_NOT_CONVERTIBLE", $"signal:{signalId}:{index}:t", "信号时间无法规范化。", timeRaw));
                var asserted = rawValue.Trim() switch
                {
                    "1" or "H" or "true" => true,
                    "0" or "L" or "false" => false,
                    _ => UnknownAsDiagnostic(rawValue, signalId, index, diagnostics)
                };
                points.Add(new SignalPoint(index, timeRaw, time.Value, time.Convertible, asserted, rawValue));
                index++;
            }
            signals[signalId] = new SignalTrace(signalId, railId, role, "s", points);
        }

        foreach (var thresholdElement in RequireArray(root, "thresholds", diagnostics))
        {
            var railId = RequireString(thresholdElement, "rail", "threshold", diagnostics);
            var version = RequireString(thresholdElement, "version", $"threshold:{railId}", diagnostics) ?? "v0";
            if (railId is null) continue;
            var rising = ParseNamed(thresholdElement, "rising", "V", diagnostics, ("mV", .001), ("V", 1));
            var falling = ParseNamed(thresholdElement, "falling", "V", diagnostics, ("mV", .001), ("V", 1));
            var hysteresis = ParseNamed(thresholdElement, "hysteresis", "V", diagnostics, ("mV", .001), ("V", 1));
            var stable = ParseNamed(thresholdElement, "minimumStable", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));
            thresholds[ThresholdKey(railId, version)] = new ThresholdSet(version, railId,
                rising.Value, rising.Raw, falling.Value, falling.Raw, hysteresis.Value, hysteresis.Raw,
                stable.Value, stable.Raw);
        }

        var edges = new List<DependencyEdge>();
        foreach (var edgeElement in RequireArray(root, "dependencies", diagnostics))
        {
            var id = RequireString(edgeElement, "id", "dependency", diagnostics);
            var from = RequireString(edgeElement, "from", $"dependency:{id}", diagnostics);
            var to = RequireString(edgeElement, "to", $"dependency:{id}", diagnostics);
            if (id is null || from is null || to is null) continue;
            var startup = ParseNamed(edgeElement, "startupMaxDelay", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));
            var shutdown = ParseNamed(edgeElement, "shutdownMaxDelay", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));
            edges.Add(new DependencyEdge(id, from, to, startup.Value, startup.Raw, shutdown.Value, shutdown.Raw,
                edgeElement.GetRawText()));
        }

        var clockElement = root.TryGetProperty("clock", out var clockJson) ? clockJson : default;
        var period = ParseNamed(clockElement, "nominalPeriod", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));
        var maxGap = ParseNamed(clockElement, "maxGap", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));

        MeasurementErrorSpec errors;
        if (root.TryGetProperty("measurementError", out var errorJson))
        {
            var voltage = ParseNamed(errorJson, "voltageAbsolute", "V", diagnostics, ("mV", .001), ("V", 1));
            var time = ParseNamed(errorJson, "timeAbsolute", "s", diagnostics, ("ms", .001), ("us", .000001), ("s", 1));
            errors = new MeasurementErrorSpec(voltage.Value, voltage.Raw, time.Value, time.Raw);
        }
        else
        {
            diagnostics.Add(new InputDiagnostic("error", "MISSING_MEASUREMENT_ERROR", "measurementError", "缺少测量误差。", null));
            errors = new MeasurementErrorSpec(0, "0 V", 0, "0 s");
        }

        return new EvidenceCase(waveforms, signals, thresholds, new DependencyGraph(edges),
            new ClockSpec("s", period.Value, period.Raw, maxGap.Value, maxGap.Raw), errors, diagnostics);
    }

    public static string ThresholdKey(string railId, string version) => $"{railId}@{version}";

    private static IEnumerable<JsonElement> RequireArray(JsonElement parent, string name, List<InputDiagnostic> diagnostics)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new InputDiagnostic("error", "MISSING_ARRAY", name, $"缺少数组字段 {name}。", null));
            return Array.Empty<JsonElement>();
        }
        return array.EnumerateArray().ToArray();
    }

    private static string? RequireString(JsonElement element, string name, string scope, List<InputDiagnostic> diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } result)
            return result;
        diagnostics.Add(new InputDiagnostic("error", "MISSING_STRING", $"{scope}:{name}", $"缺少非空字符串字段 {name}。", null));
        return null;
    }

    private static PhysicalQuantity ParseNamed(JsonElement element, string name, string unit,
        List<InputDiagnostic> diagnostics, params (string Unit, double Factor)[] known)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new InputDiagnostic("error", "MISSING_QUANTITY", name, $"缺少数值字段 {name}。", null));
            return new PhysicalQuantity(double.NaN, unit, "", false);
        }
        var raw = value.GetString() ?? "";
        var parsed = PhysicalQuantity.Parse(raw, unit, known);
        if (!parsed.Convertible || double.IsNaN(parsed.Value))
            diagnostics.Add(new InputDiagnostic("error", "QUANTITY_NOT_CONVERTIBLE", name, $"数值无法规范化，原始值保留：{raw}。", raw));
        return parsed;
    }

    private static bool UnknownAsDiagnostic(string raw, string signalId, int index, List<InputDiagnostic> diagnostics)
    {
        diagnostics.Add(new InputDiagnostic("unknown", "SIGNAL_VALUE_UNKNOWN", $"signal:{signalId}:{index}:v",
            "信号值不是已知逻辑电平，按未知处理并保留原值。", raw));
        return false;
    }
}
