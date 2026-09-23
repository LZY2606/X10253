using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSeq.Core;

/// <summary>Stable, content-addressed identities. Same content => same id, forever.</summary>
public static class Ids
{
    public static string Compute(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}

public static class Canon
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}

/// <summary>A raw sample exactly as imported. Unit/基准 lives on the waveform, never rewritten here.</summary>
public sealed record SamplePoint(double T, double V);

public sealed record RailWaveform(
    string Rail,
    string Role,          // "rail" | "enable" | "good"
    string ValueUnit,     // "mV" | "V" | anything else -> diagnostic, kept raw
    string TimeUnit,      // "ms" expected; anything else -> diagnostic, kept raw
    double SamplePeriod,  // in TimeUnit
    double ValueError,    // measurement error, in ValueUnit
    IReadOnlyList<SamplePoint> Samples);

public sealed record WaveformSet(
    string Id,
    string? ParentId,
    string Label,
    string Author,
    string CreatedAt,
    IReadOnlyList<RailWaveform> Rails)
{
    public static WaveformSet Create(string? parentId, string label, string author, string createdAt, IReadOnlyList<RailWaveform> rails)
    {
        var canonical = Canon.Json(new { parentId, label, author, createdAt, rails });
        return new WaveformSet(Ids.Compute("wv", canonical), parentId, label, author, createdAt, rails);
    }
}

/// <summary>Rising and falling use different thresholds; the gap between them is the hysteresis band.</summary>
public sealed record ThresholdSpec(
    string Rail,
    double RiseThresholdMv,
    double FallThresholdMv,
    double MinStableMs);

public sealed record ThresholdSet(
    string Id,
    string? ParentId,
    string Label,
    string Author,
    string CreatedAt,
    IReadOnlyList<ThresholdSpec> Specs)
{
    public static ThresholdSet Create(string? parentId, string label, string author, string createdAt, IReadOnlyList<ThresholdSpec> specs)
    {
        var canonical = Canon.Json(new { parentId, label, author, createdAt, specs });
        return new ThresholdSet(Ids.Compute("th", canonical), parentId, label, author, createdAt, specs);
    }

    public ThresholdSpec? For(string rail) => Specs.FirstOrDefault(s => s.Rail == rail);
}

/// <summary>Edge: on power-up, After may rise only once Before is stably on (+MinDelay). On shutdown the order reverses.</summary>
public sealed record DependencyEdge(
    string Id,
    string Before,
    string After,
    double MinDelayMs,
    double MaxDelayMs)
{
    public static DependencyEdge Create(string before, string after, double minDelayMs, double maxDelayMs)
    {
        var canonical = Canon.Json(new { before, after, minDelayMs, maxDelayMs });
        return new DependencyEdge(Ids.Compute("de", canonical), before, after, minDelayMs, maxDelayMs);
    }
}

public sealed record DependencyGraph(
    string Id,
    string? ParentId,
    string Label,
    string Author,
    string CreatedAt,
    IReadOnlyList<DependencyEdge> Edges)
{
    public static DependencyGraph Create(string? parentId, string label, string author, string createdAt, IReadOnlyList<DependencyEdge> edges)
    {
        var canonical = Canon.Json(new { parentId, label, author, createdAt, edges });
        return new DependencyGraph(Ids.Compute("dg", canonical), parentId, label, author, createdAt, edges);
    }
}

/// <summary>Per-rail scope fingerprint: which exact source content a derived result depends on.</summary>
public sealed record RailScope(string Rail, string WaveHash, string ThresholdHash);

public sealed record DerivedResult(
    string Id,
    string Kind,              // "state-windows" | "sequence-report" | "cycle-report"
    string AlgorithmVersion,
    string ParamFingerprint,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<RailScope> Scope,
    string PayloadJson,
    string CreatedAt)
{
    public static DerivedResult Create(string kind, string alg, string fingerprint,
        IReadOnlyList<string> sourceIds, IReadOnlyList<RailScope> scope, object payload, string createdAt)
    {
        var payloadJson = Canon.Json(payload);
        var canonical = Canon.Json(new { kind, alg = alg, fingerprint, sourceIds, scope, payloadJson });
        return new DerivedResult(Ids.Compute("rs", canonical), kind, alg, fingerprint, sourceIds, scope, payloadJson, createdAt);
    }
}

public sealed record Decision(
    string Id,
    string TargetResultId,
    string Verdict,      // "accept" | "reject" | "adjust"
    string Rationale,
    string Author,
    string CreatedAt,
    string? UndoneBy)
{
    public static Decision Create(string targetResultId, string verdict, string rationale, string author, string createdAt)
    {
        var canonical = Canon.Json(new { targetResultId, verdict, rationale, author, createdAt });
        return new Decision(Ids.Compute("dc", canonical), targetResultId, verdict, rationale, author, createdAt, null);
    }
}

public sealed record EventRecord(long Seq, string Id, string Ts, string Type, string Json);

public sealed record InputRecord(string Kind, string Id, string? ParentId, string Json, string CreatedAt, long Seq);
