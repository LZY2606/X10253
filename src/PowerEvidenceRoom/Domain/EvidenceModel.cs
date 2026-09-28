using System.Text.Json.Nodes;

namespace PowerEvidenceRoom.Domain;

public enum SourceKind
{
    Package,
    RailCatalog,
    Waveform,
    Signal,
    ThresholdSet,
    ConstraintGraph,
    SamplingClock,
    MeasurementModel
}

public enum CandidateOutcome
{
    Pending,
    Accepted,
    Rejected,
    Unknown,
    Conflict
}

public enum ResultFreshness
{
    Current,
    Stale,
    Historical
}

public sealed record SourceRef(
    string SourceId,
    SourceKind Kind,
    string Subject,
    int Version,
    string Fingerprint)
{
    public string StableIdentity => $"{Kind}:{Subject}:v{Version}:{Fingerprint[..12]}";
}

public sealed record SourceDocument(
    string SourceId,
    SourceKind Kind,
    string Subject,
    int Version,
    int? ParentVersion,
    string Fingerprint,
    JsonNode Original,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public SourceRef ToRef() => new(SourceId, Kind, Subject, Version, Fingerprint);
}

public sealed record Diagnostic(
    string Severity,
    string Code,
    string Message,
    string? SourceId,
    string? Coordinate)
{
    public bool BlocksImport => Severity.Equals("error", StringComparison.OrdinalIgnoreCase);
}

public sealed record Evidence(
    SourceRef Source,
    string Coordinate,
    string RawValue,
    string? NormalizedValue,
    string? Unit,
    string Role)
{
    public string StableIdentity => $"{Source.StableIdentity}@{Coordinate}:{RawValue}";
}

public sealed record Candidate(
    string CandidateId,
    string ResultId,
    string Kind,
    string Title,
    CandidateOutcome Outcome,
    ResultFreshness Freshness,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<SourceRef> DirectSources,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> AcceptedReasons,
    JsonNode Basis,
    IReadOnlyList<string> AlternativeCandidateIds,
    string? DecisionEventId,
    double? NumericValue,
    string? Unit)
{
    public string StableIdentity => CandidateId;
}

public sealed record StateWindow(
    string RailId,
    string State,
    long StartNs,
    long? EndNs,
    long? DurationNs,
    string Status,
    IReadOnlyList<SourceRef> DirectSources,
    IReadOnlyList<Evidence> Evidence,
    JsonNode Basis)
{
    public string StableIdentity => $"window:{RailId}:{State}:{StartNs}:{EndNs?.ToString() ?? "open"}";
}

public sealed record Violation(
    string ViolationId,
    string CandidateId,
    string Kind,
    string Severity,
    long FirstEvidenceAtNs,
    string Message,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<SourceRef> DirectSources,
    JsonNode Basis)
{
    public string StableIdentity => ViolationId;
}

public sealed record ClockGap(
    string GapId,
    long StartNs,
    long EndNs,
    long DurationNs,
    string Status,
    IReadOnlyList<SourceRef> DirectSources,
    IReadOnlyList<Evidence> Evidence)
{
    public string StableIdentity => GapId;
}

public sealed record GraphCycle(
    string CycleId,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<SourceRef> DirectSources,
    JsonNode Basis)
{
    public string StableIdentity => CycleId;
}

public sealed record AnalysisResult
{
    public required string ResultId { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string ParameterFingerprint { get; init; }
    public required IReadOnlyList<SourceRef> DirectSources { get; init; }
    public required IReadOnlyList<StateWindow> Windows { get; init; }
    public required IReadOnlyList<Candidate> Candidates { get; init; }
    public required IReadOnlyList<Violation> Violations { get; init; }
    public required IReadOnlyList<ClockGap> ClockGaps { get; init; }
    public required IReadOnlyList<GraphCycle> Cycles { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public int BirthEventSeq { get; init; }
    public string? BirthEventId { get; init; }
    public Violation? FirstViolation => Violations
        .OrderBy(v => v.FirstEvidenceAtNs)
        .ThenBy(v => v.ViolationId, StringComparer.Ordinal)
        .FirstOrDefault();
}

public sealed record ProjectEvent(
    int Seq,
    string EventId,
    string Type,
    string AtVirtualTime,
    string Actor,
    string? ParentEventId,
    JsonNode Payload)
{
    public string StableIdentity => EventId;
}

public sealed record DecisionRecord(
    string DecisionEventId,
    string CandidateId,
    string ResultId,
    bool Accept,
    string Rationale,
    string Actor,
    bool Reversed,
    string? ReversalEventId,
    IReadOnlyList<SourceRef> DirectSources);

public sealed record ProjectView(
    ProjectState State,
    int EventSeq,
    IReadOnlyList<ProjectEvent> Events,
    AnalysisResult? Analysis,
    IReadOnlyList<AnalysisResult> AllAnalyses,
    IReadOnlyList<Candidate> VisibleCandidates,
    IReadOnlyList<AnalysisResult> StaleAnalyses);
