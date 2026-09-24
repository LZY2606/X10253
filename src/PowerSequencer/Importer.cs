using System.Globalization;
using System.Text.Json.Nodes;

namespace PowerSequencer;

public sealed class ImportException : Exception
{
    public List<string> Errors { get; }
    public ImportException(List<string> errors) : base(string.Join("; ", errors)) => Errors = errors;
}

public sealed record ParsedWaveformSet(
    List<RailSeries> Rails,
    List<DigitalSeries> Signals,
    ClockSpec Clock,
    List<string> Diagnostics);

public sealed record ParsedThresholdSet(Dictionary<string, RailThreshold> Rails, List<string> Diagnostics);

public sealed record ParsedDependencySet(List<DependencyEdge> Edges, List<string> Diagnostics);

/// <summary>
/// Import layer. The submitted document is stored verbatim (immutable); parsing
/// only reads it. Values that cannot be converted stay in their original
/// position inside the stored raw document and are reported as diagnostics —
/// they are never silently dropped to make plotting easier.
/// </summary>
public static class Importer
{
    public static JsonObject ParseEnvelope(string json, out string kind, out string label, out string? parent)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new ImportException(new List<string> { "document must be a JSON object" });
        }
        catch (ImportException) { throw; }
        catch (Exception ex)
        {
            throw new ImportException(new List<string> { $"invalid JSON: {ex.Message}" });
        }

        var errors = new List<string>();
        kind = (string?)root["kind"] ?? "";
        if (kind is not ("waveform-set" or "threshold-set" or "dependency-set"))
            errors.Add($"field 'kind' must be one of waveform-set|threshold-set|dependency-set, got '{kind}'");
        label = (string?)root["label"] ?? "";
        if (string.IsNullOrWhiteSpace(label))
            errors.Add("field 'label' is required");
        parent = (string?)root["parent"];
        if (root["data"] is not JsonObject)
            errors.Add("field 'data' must be an object");
        if (errors.Count > 0)
            throw new ImportException(errors);
        return root;
    }

    private static bool TryNum(JsonNode? node, out double value, out string raw)
    {
        raw = node?.ToJsonString() ?? "null";
        value = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) { value = d; return true; }
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            { value = parsed; return true; }
        }
        return false;
    }

    public static ParsedWaveformSet ParseWaveformSet(JsonObject data)
    {
        var errors = new List<string>();
        var diags = new List<string>();
        var rails = new List<RailSeries>();
        var signals = new List<DigitalSeries>();

        ClockSpec? clock = null;
        if (data["clock"] is JsonObject c)
        {
            if (!TryNum(c["periodNs"], out var period, _) || period <= 0)
                errors.Add("clock.periodNs must be a positive number");
            TryNum(c["jitterNs"], out var jitter, _);
            TryNum(c["errorV"], out var errV, _);
            if (errors.Count == 0)
                clock = new ClockSpec(period, jitter, errV, c.ToJsonString());
        }
        else errors.Add("clock object is required (periodNs, jitterNs, errorV)");

        if (data["rails"] is not JsonArray railArr || railArr.Count == 0)
            errors.Add("rails must be a non-empty array");
        else
        {
            var ri = 0;
            foreach (var node in railArr)
            {
                ri++;
                if (node is not JsonObject r) { errors.Add($"rails[{ri}] must be an object"); continue; }
                var name = (string?)r["rail"];
                if (string.IsNullOrWhiteSpace(name)) { errors.Add($"rails[{ri}].rail is required"); continue; }
                var vUnit = (string?)r["voltageUnit"] ?? "V";
                var tUnit = (string?)r["timeUnit"] ?? "ns";
                var rd = new List<string>();
                var pts = new List<SamplePoint>();
                if (r["samples"] is not JsonArray samples || samples.Count < 2)
                    errors.Add($"rail '{name}': samples must be an array of at least 2 [t,v] pairs");
                else
                {
                    var si = 0;
                    foreach (var sn in samples)
                    {
                        si++;
                        if (sn is not JsonArray pair || pair.Count != 2)
                        {
                            rd.Add($"rail '{name}' sample {si}: not a [t,v] pair, kept raw at original position");
                            continue;
                        }
                        var okT = TryNum(pair[0], out var t, out var rawT);
                        var okV = TryNum(pair[1], out var v, out var rawV);
                        if (!okT || !okV)
                        {
                            rd.Add($"rail '{name}' sample {si}: {(okT ? "" : $"time '{rawT}' not convertible")}" +
                                $"{(!okT && !okV ? "; " : "")}{(okV ? "" : $"value '{rawV}' not convertible")}; kept raw at original position");
                            continue;
                        }
                        pts.Add(new SamplePoint(t, v, rawT, rawV));
                    }
                    if (pts.Count < 2)
                        errors.Add($"rail '{name}': fewer than 2 convertible samples");
                }
                rails.Add(new RailSeries(name!, vUnit, tUnit,
                    pts.OrderBy(p => p.T).ToList(), rd));
                diags.AddRange(rd);
            }
        }

        if (data["signals"] is JsonArray sigArr)
        {
            var si = 0;
            foreach (var node in sigArr)
            {
                si++;
                if (node is not JsonObject s) { errors.Add($"signals[{si}] must be an object"); continue; }
                var name = (string?)s["name"] ?? $"signal{si}";
                var kind = (string?)s["kind"] ?? "enable";
                var sd = new List<string>();
                var pts = new List<SamplePoint>();
                if (s["samples"] is JsonArray samples)
                {
                    var pi = 0;
                    foreach (var sn in samples)
                    {
                        pi++;
                        if (sn is not JsonArray pair || pair.Count != 2)
                        {
                            sd.Add($"signal '{name}' sample {pi}: not a [t,v] pair, kept raw");
                            continue;
                        }
                        if (!TryNum(pair[0], out var t, out var rawT) || !TryNum(pair[1], out var v, out var rawV))
                        {
                            sd.Add($"signal '{name}' sample {pi}: not convertible, kept raw");
                            continue;
                        }
                        pts.Add(new SamplePoint(t, v, rawT, rawV));
                    }
                }
                signals.Add(new DigitalSeries(name, kind, "ns", pts.OrderBy(p => p.T).ToList(), sd));
                diags.AddRange(sd);
            }
        }

        if (errors.Count > 0) throw new ImportException(errors);
        return new ParsedWaveformSet(rails, signals, clock!, diags);
    }

    public static ParsedThresholdSet ParseThresholdSet(JsonObject data)
    {
        var errors = new List<string>();
        var rails = new Dictionary<string, RailThreshold>();
        if (data["rails"] is not JsonObject obj || obj.Count == 0)
            errors.Add("data.rails must be a non-empty object keyed by rail name");
        else
        {
            foreach (var kv in obj)
            {
                if (kv.Value is not JsonObject t) { errors.Add($"thresholds for '{kv.Key}' must be an object"); continue; }
                var ok = true;
                double Need(string field)
                {
                    if (!TryNum(t[field], out var x, _)) { errors.Add($"rail '{kv.Key}': field '{field}' must be numeric"); ok = false; }
                    return x;
                }
                var rise = Need("riseV");
                var fall = Need("fallV");
                var hyst = Need("hysteresisV");
                var minStable = Need("minStableNs");
                if (!ok) continue;
                if (rise <= fall)
                    errors.Add($"rail '{kv.Key}': riseV ({rise}) must be greater than fallV ({fall}) to provide hysteresis");
                else if (minStable <= 0)
                    errors.Add($"rail '{kv.Key}': minStableNs must be positive");
                else
                    rails[kv.Key] = new RailThreshold(kv.Key, rise, fall, hyst, minStable);
            }
        }
        if (errors.Count > 0) throw new ImportException(errors);
        return new ParsedThresholdSet(rails, new List<string>());
    }

    public static ParsedDependencySet ParseDependencySet(JsonObject data)
    {
        var errors = new List<string>();
        var edges = new List<DependencyEdge>();
        if (data["edges"] is not JsonArray arr)
            errors.Add("data.edges must be an array");
        else
        {
            var i = 0;
            foreach (var node in arr)
            {
                i++;
                if (node is not JsonObject e) { errors.Add($"edges[{i}] must be an object"); continue; }
                var from = (string?)e["from"];
                var to = (string?)e["to"];
                var kind = (string?)e["kind"] ?? "rise-after";
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                { errors.Add($"edges[{i}]: 'from' and 'to' are required"); continue; }
                double? maxDelay = null;
                if (e["maxDelayNs"] is not null)
                {
                    if (TryNum(e["maxDelayNs"], out var md, _)) maxDelay = md;
                    else errors.Add($"edges[{i}]: maxDelayNs must be numeric");
                }
                edges.Add(new DependencyEdge(from!, to!, kind, maxDelay));
            }
        }
        if (errors.Count > 0) throw new ImportException(errors);
        return new ParsedDependencySet(edges, new List<string>());
    }
}
