using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSequencer.Core;

public static class Algo
{
    public const string WindowEval = "window-eval@1.0.0";
    public const string SeqAnalyze = "seq-analyze@1.0.0";
    public const string CycleCheck = "cycle-check@1.0.0";
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public static class Ids
{
    public static string Of(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return prefix + "_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string Canonical<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOpts.Canonical);
}

/// <summary>A diagnostic attached to raw input that could not be normalized.
/// The raw value stays in place; normalization happens only in the derived layer.</summary>
public sealed record ImportDiagnostic(int? Line, string Message, string Raw);

// ---------- Raw inputs (immutable, content addressed) ----------

public sealed record SamplePoint(double Time, double Voltage, string RawTime, string RawVoltage);

public sealed record Waveform(
    string Id, string? ParentId, int Version, string Rail, string Signal,
    string TimeUnit, string VoltageUnit,
    List<SamplePoint> Points, List<ImportDiagnostic> Diagnostics, string RawText);

public sealed record RailThreshold(string Rail, double RiseV, double FallV, double HysteresisV, double MinStableUs);

public sealed record ThresholdSet(
    string Id, string? ParentId, int Version,
    Dictionary<string, RailThreshold> Rails, string RawText);

public sealed record DepEdge(string Before, string After);

public sealed record DependencyGraphInput(
    string Id, string? ParentId, int Version, List<DepEdge> Edges, string RawText);

public sealed record ClockSpec(string Id, double NominalPeriodUs, double MaxGapFactor, string RawText);

public sealed record ErrorSpec(string Id, double VoltageTolV, double TimeTolUs, string RawText);

// ---------- Derived results (algorithm versioned, source linked) ----------

public sealed record Judgment(string Rule, string Summary, Dictionary<string, string> Data);

public sealed record StateWindow(string State, double FromUs, double ToUs, string Basis);

public sealed record Excursion(
    string Id, string Rail, string Kind, double StartUs, double EndUs,
    double DurationUs, string Verdict, string Reason);

public sealed record WindowPayload(
    string Rail,
    List<StateWindow> Windows,
    List<Excursion> Excursions,
    List<Judgment> Judgments,
    List<string> Diagnostics);

public sealed record EdgeCheck(
    string Before, string After, string Phase, double? DiffUs, double TolUs,
    string Verdict, List<string> Candidates, string Reason);

public sealed record Violation(string Rule, double TimeUs, string Summary, List<string> Sources);

public sealed record SeqPayload(
    Dictionary<string, double?> GoodAtUs,
    Dictionary<string, double?> OffAtUs,
    List<EdgeCheck> Checks,
    Violation? FirstViolation,
    List<Judgment> Judgments);

public sealed record CyclePayload(
    List<List<DepEdge>> Cycles,
    List<List<DepEdge>> MinimalEdgeSets,
    List<Judgment> Judgments);

// ---------- Store rows ----------

public sealed record InputRow(string Id, string Kind, string? ParentId, int Version, string Json, string Raw, long Seq);

public sealed record DerivedRow(
    string Id, string Algo, string Scope, string Fingerprint,
    List<string> Sources, string Payload, long Seq);

public sealed record EventRow(long Seq, long Tick, string Kind, string Payload);

public sealed record DecisionRow(long Seq, string Target, string Verdict, string Rationale, bool Undone);

public sealed record Snapshot(
    long Seq,
    Dictionary<string, Waveform> Waveforms,
    ThresholdSet? Thresholds,
    DependencyGraphInput? Deps,
    ClockSpec? Clock,
    ErrorSpec? Error,
    List<DerivedRow> Current,
    List<DerivedRow> Stale,
    List<DecisionRow> Decisions,
    List<EventRow> Conflicts);
