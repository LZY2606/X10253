using System.Text.Json;
using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Analysis;

public static class EngineInfo
{
    /// <summary>Power sequencing state-machine algorithm version. Bumped only when judgment semantics change.</summary>
    public const string AlgorithmVersion = "psm-1.1.0";
}

public enum EstimateKind
{
    Known,
    Ambiguous,
    Unknown
}

public sealed record CrossingEstimate(
    string RailId,
    string Phase,
    string ThresholdKind,
    double ThresholdVolts,
    double? NominalSeconds,
    double? EarliestSeconds,
    double? LatestSeconds,
    EstimateKind Kind,
    string BasisRule,
    IReadOnlyList<string> SourceSampleIds,
    string? ClockGapId,
    string? Reason);

public sealed record StateWindow(
    string State,
    double? StartSeconds,
    double? EndSeconds,
    string Source,
    string Reason);

public sealed record GlitchEvent(
    string EventId,
    string RailId,
    string ThresholdKind,
    double StartSeconds,
    double EndSeconds,
    double DurationSeconds,
    double MinStableSeconds,
    string Classification,
    string Rule,
    IReadOnlyList<string> SourceSampleIds);

public sealed record SignalJudgment(
    string SignalId,
    string RailId,
    string Kind,
    double? EventSeconds,
    double? ReferenceSeconds,
    string Status,
    string Reason);

public sealed record RailWindowResult(
    string RailId,
    string RailVersionId,
    IReadOnlyList<StateWindow> Windows,
    CrossingEstimate? Rise,
    CrossingEstimate? Fall,
    double? StableConfirmedSeconds,
    IReadOnlyList<GlitchEvent> Glitches,
    IReadOnlyList<SignalJudgment> SignalJudgments,
    IReadOnlyList<DiagnosticEvent> Diagnostics,
    string ParameterFingerprint);

public sealed record DiagnosticEvent(
    string Code,
    string SourceId,
    string Message,
    string? RawValue,
    string Retention);

public sealed record LegalOrdering(IReadOnlyList<string> RailOrder, string Rationale);

public sealed record EdgeCheckResult(
    string EdgeId,
    string EdgeVersionId,
    string Phase,
    string RequiredOrder,
    double MinDelaySeconds,
    double? ObservedDelaySeconds,
    double? DelayErrorSeconds,
    string Status,
    string Reason,
    string Rule,
    IReadOnlyList<LegalOrdering> LegalOrderings,
    IReadOnlyList<string> CrossingRefs);

public sealed record FeedbackSetCandidate(
    string SetId,
    IReadOnlyList<string> EdgeIds,
    int Size,
    string Proof,
    bool UniquelyMinimum);

public sealed record CycleAnalysisResult(
    bool HasCycle,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<FeedbackSetCandidate> MinimumFeedbackSets,
    IReadOnlyList<string> ActiveEdgeIds,
    IReadOnlyList<string> WaivedEdgeIds,
    string Rule);

public sealed record FirstViolationEvidence(
    int Rank,
    string Category,
    string CandidateId,
    string Phase,
    double? EvidenceSeconds,
    string Title,
    JsonElement Detail,
    IReadOnlyList<string> DirectSourceIds);

public sealed record EngineCandidate(
    string CandidateId,
    string Category,
    string Title,
    JsonElement Detail,
    string FirstResultId,
    IReadOnlyList<string> DirectSourceIds,
    string Severity);

public sealed record CommitAnalysis(
    string CommitId,
    string Branch,
    IReadOnlyDictionary<string, RailWindowResult> Rails,
    IReadOnlyList<EdgeCheckResult> EdgeChecks,
    CycleAnalysisResult Cycle,
    FirstViolationEvidence? FirstViolation,
    IReadOnlyList<EngineCandidate> Candidates,
    string ParameterFingerprint,
    IReadOnlyDictionary<string, string> RailVersions,
    IReadOnlyDictionary<string, string> EdgeVersions);
