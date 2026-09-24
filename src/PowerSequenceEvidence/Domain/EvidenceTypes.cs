using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerSequenceEvidence.Domain;

public enum DocumentKind
{
    RailWaveform,
    SignalTrace,
    ThresholdSet,
    DependencyGraph,
    Clock,
    MeasurementError
}

public enum AnalysisStatus
{
    Accepted,
    Rejected,
    Candidate,
    Unknown
}

public enum EventSeverity
{
    Info,
    Candidate,
    Rejected,
    Violation,
    Unknown
}

public sealed record StoredDocument(
    string Id,
    DocumentKind Kind,
    string Scope,
    string? ParentDocumentId,
    string RawText,
    string ContentHash,
    string MediaType,
    int Sequence,
    DateTimeOffset CreatedAt);

public sealed record PhysicalQuantity(
    double Value,
    string Unit,
    string Raw,
    bool Convertible)
{
    public static PhysicalQuantity Parse(string raw, string canonicalUnit, params (string Unit, double Factor)[] known)
    {
        var text = raw.Trim();
        var unitPart = new string(text.SkipWhile(char.IsDigit).SkipWhile(ch => ch is '.' or '-' or '+').ToArray()).Trim();
        var numberPart = text[..(text.Length - unitPart.Length)].Trim();
        if (!double.TryParse(numberPart, CultureInfo.InvariantCulture, out var value))
            return new PhysicalQuantity(double.NaN, canonicalUnit, raw, false);

        if (string.IsNullOrWhiteSpace(unitPart))
            return new PhysicalQuantity(value, canonicalUnit, raw, true);

        var match = known.FirstOrDefault(k => string.Equals(k.Unit, unitPart, StringComparison.OrdinalIgnoreCase));
        if (match.Unit is null)
            return new PhysicalQuantity(double.NaN, canonicalUnit, raw, false);
        return new PhysicalQuantity(value * match.Factor, canonicalUnit, raw, true);
    }

    public override string ToString() => Raw;
}

public sealed record RailSample(
    int Index,
    string TimeRaw,
    double TimeSeconds,
    bool TimeConvertible,
    string VoltageRaw,
    double VoltageVolts,
    bool VoltageConvertible);

public sealed record RailWaveform(string RailId, string TimeBase, IReadOnlyList<RailSample> Samples)
{
    public double? MinTime => Samples.Where(s => s.TimeConvertible).Select(s => s.TimeSeconds).DefaultIfEmpty().Min();
    public double? MaxTime => Samples.Where(s => s.TimeConvertible).Select(s => s.TimeSeconds).DefaultIfEmpty().Max();
}

public sealed record SignalPoint(
    int Index,
    string TimeRaw,
    double TimeSeconds,
    bool TimeConvertible,
    bool Asserted,
    string RawValue);

public sealed record SignalTrace(string SignalId, string RailId, string Role, string TimeBase, IReadOnlyList<SignalPoint> Points);

public sealed record ThresholdSet(
    string Version,
    string RailId,
    double RisingVolts,
    string RisingRaw,
    double FallingVolts,
    string FallingRaw,
    double HysteresisVolts,
    string HysteresisRaw,
    double MinimumStableSeconds,
    string MinimumStableRaw);

public sealed record DependencyEdge(
    string Id,
    string FromRail,
    string ToRail,
    double StartupMaxDelaySeconds,
    string StartupMaxDelayRaw,
    double ShutdownMaxDelaySeconds,
    string ShutdownMaxDelayRaw,
    string SourceRaw);

public sealed record DependencyGraph(IReadOnlyList<DependencyEdge> Edges);

public sealed record ClockSpec(
    string TimeBase,
    double NominalPeriodSeconds,
    string NominalPeriodRaw,
    double MaxGapSeconds,
    string MaxGapRaw);

public sealed record MeasurementErrorSpec(
    double VoltageAbsoluteVolts,
    string VoltageAbsoluteRaw,
    double TimeAbsoluteSeconds,
    string TimeAbsoluteRaw);

public sealed record EvidenceCase(
    IReadOnlyDictionary<string, RailWaveform> Waveforms,
    IReadOnlyDictionary<string, SignalTrace> Signals,
    IReadOnlyDictionary<string, ThresholdSet> Thresholds,
    DependencyGraph Graph,
    ClockSpec Clock,
    MeasurementErrorSpec MeasurementError,
    IReadOnlyList<InputDiagnostic> Diagnostics);

public sealed record InputDiagnostic(string Severity, string Code, string Scope, string Message, string? RawValue);

public sealed record EvidenceEvent(
    string EventId,
    string CaseId,
    string Type,
    EventSeverity Severity,
    AnalysisStatus Status,
    string Message,
    double? StartSeconds,
    double? EndSeconds,
    string? RailId,
    string? SignalId,
    string? EdgeId,
    IReadOnlyList<string> DirectSources,
    string AlgorithmVersion,
    string ParameterFingerprint,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<string>? CandidateIds = null,
    IReadOnlyList<string>? RejectionReasons = null);

public sealed record RailStateWindow(
    string WindowId,
    string RailId,
    string State,
    int StartSampleIndex,
    int EndSampleIndex,
    double StartSeconds,
    double EndSeconds,
    bool FromRisingThreshold,
    IReadOnlyList<string> DirectSources,
    AnalysisStatus Status,
    string? RejectionReason,
    string StableIdentity);

public sealed record SequenceViolation(
    string ViolationId,
    string Phase,
    string EdgeId,
    string FromRail,
    string ToRail,
    double ObservedDelaySeconds,
    double AllowedDelaySeconds,
    double MeasurementUncertaintySeconds,
    double EvidenceTimeSeconds,
    AnalysisStatus Status,
    string? RejectionReason,
    IReadOnlyList<string> DirectSources,
    string StableIdentity);

public sealed record CycleCandidate(
    string CandidateId,
    IReadOnlyList<string> EdgeIds,
    bool Adopted,
    string? RejectionReason,
    string StableIdentity);

public sealed record ClockGap(
    string GapId,
    double StartSeconds,
    double EndSeconds,
    double GapSeconds,
    bool ExceedsLimit,
    string StableIdentity);

public sealed record AnalysisResult(
    string CaseId,
    AnalysisStatus OverallStatus,
    string? FirstViolationEventId,
    IReadOnlyList<RailStateWindow> Windows,
    IReadOnlyList<SequenceViolation> Violations,
    IReadOnlyList<CycleCandidate> CycleCandidates,
    IReadOnlyList<ClockGap> ClockGaps,
    IReadOnlyList<EvidenceEvent> Events,
    IReadOnlyDictionary<string, string> DirectSources,
    string AlgorithmVersion,
    string ParameterFingerprint);

public sealed record ProjectEvent(
    long Sequence,
    string EventId,
    string Kind,
    string CaseId,
    string? DocumentId,
    string? ParentEventId,
    string PayloadJson,
    string Actor,
    DateTimeOffset OccurredAt)
{
    public JsonDocument Payload() => JsonDocument.Parse(PayloadJson);
}

public sealed record DecisionRequest(string Decision, string Reason, string? TargetId);

[JsonSerializable(typeof(EvidenceCase))]
[JsonSerializable(typeof(EvidenceEvent))]
[JsonSerializable(typeof(AnalysisResult))]
internal sealed partial class DomainJsonContext : JsonSerializerContext;
