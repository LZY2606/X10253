using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvidenceRoom;

/// <summary>A domain quantity: raw representation is preserved; normalization happens in the derived layer.</summary>
public sealed record Quantity(string Raw, string Unit, string Dim)
{
    public double? Normalized { get; init; }
    public string? Diagnostic { get; init; }

    public static Quantity Parse(string raw, string dim)
    {
        var (value, unit, error) = Units.Parse(raw);
        var q = new Quantity(raw, unit ?? "", dim);
        if (error is not null)
            return q with { Diagnostic = $"无法解析: {error}" };
        if (!Units.TryNormalize(value!.Value, unit!, dim, out var baseValue, out var dimErr))
            return q with { Unit = unit!, Diagnostic = $"无法转换到{dim}基准单位: {dimErr}" };
        return q with { Unit = unit!, Normalized = baseValue };
    }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(new
    {
        raw = Raw,
        unit = Unit,
        dim = Dim,
        normalized = Normalized,
        diagnostic = Diagnostic
    });
}

public static class Units
{
    private static readonly Dictionary<string, (string Dim, double Factor)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"] = ("voltage", 1.0), ["mV"] = ("voltage", 1e-3), ["uV"] = ("voltage", 1e-6), ["kV"] = ("voltage", 1e3),
        ["s"] = ("time", 1.0), ["ms"] = ("time", 1e-3), ["us"] = ("time", 1e-6), ["ns"] = ("time", 1e-9),
    };

    public static (double? Value, string? Unit, string? Error) Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null, "空值");
        var s = raw.Trim();
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        // careful: "1e-3s" — exponent sign consumed above; split number/unit
        var numPart = s[..i];
        var unitPart = s[i..].Trim();
        if (!double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return (null, null, $"数值部分 '{numPart}' 无法解析");
        if (unitPart.Length == 0) return (v, "", null); // unitless: assumed already base
        return (v, unitPart, null);
    }

    public static bool TryNormalize(double value, string unit, string dim, out double baseValue, out string? error)
    {
        baseValue = 0; error = null;
        if (unit.Length == 0) { baseValue = value; return true; } // already base unit
        if (!Table.TryGetValue(unit, out var e)) { error = $"未知单位 '{unit}'"; return false; }
        if (e.Dim != dim) { error = $"单位 '{unit}' 的量纲是 {e.Dim}，期望 {dim}"; return false; }
        baseValue = value * e.Factor;
        return true;
    }
}

public sealed record SamplePoint(Quantity T, Quantity V);

public sealed record RailWaveform(
    string Name,
    string Unit,
    List<SamplePoint> Samples,
    List<(Quantity T, string Level)> Enable,
    List<(Quantity T, string Level)> Good);

public sealed record WaveformPayload(
    string TimeBasis,
    Quantity ClockInterval,
    Quantity VoltageError,
    List<RailWaveform> Rails);

public sealed record RailThreshold(Quantity Rise, Quantity Fall, Quantity MinStable)
{
    public double Hysteresis => (Rise.Normalized ?? 0) - (Fall.Normalized ?? 0);
}

public sealed record ThresholdPayload(Dictionary<string, RailThreshold> Rails);

public sealed record DependencyEdge(string From, string To, Quantity MinDelay, string Phase);

public sealed record DependencyPayload(List<DependencyEdge> Edges);

/// <summary>Immutable versioned input. Revisions create a new version with ParentId set.</summary>
public sealed record InputVersion(
    string Id, string? ParentId, string Kind, string Label, string PayloadJson, long Seq);

/// <summary>First-class derived result: candidate / rejection / error / unknown all live here.</summary>
public sealed record DerivedResult(
    string Id,
    string Kind,          // rail-windows | startup-check | shutdown-check | cycle-check | first-violation
    string AlgoVersion,
    string Fingerprint,   // parameter fingerprint
    string SourcesJson,   // [{kind, rail, id}]
    string Status,        // current | stale
    string PayloadJson,
    long Seq,
    long EventSeq = 0);

public sealed record EventRecord(long Seq, string Id, string At, string Kind, string PayloadJson);

public static class Ids
{
    public static string Stable(string prefix, string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"{prefix}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public static string Fingerprint(object o)
    {
        var json = JsonSerializer.Serialize(o);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }
}

public static class J
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Ser<T>(T v) => JsonSerializer.Serialize(v, Opts);
    public static T? De<T>(string json) => JsonSerializer.Deserialize<T>(json, Opts);
    public static JsonElement Elem<T>(T v) => JsonSerializer.SerializeToElement(v, Opts);
}
