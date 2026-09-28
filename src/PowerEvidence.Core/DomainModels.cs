using System.Globalization;
using System.Text.Json.Serialization;

namespace PowerEvidence.Core;

public enum Severity { Info, Warning, Error }
public enum CandidateStatus { Proposed, Accepted, Rejected, Contested, Stale, Unknown }
public enum DecisionOutcome { Accept, Reject, Adjust }

public sealed record EventEnvelope(
    string EventId,
    string ProjectId,
    int Sequence,
    string OccurredAt,
    string Type,
    string ParentEventId,
    JsonElement Payload);

public sealed record StableIdentity(string Id, string Scope, string SemanticKey);

public sealed record AlgorithmProvenance(
    string AlgorithmVersion,
    string ParameterFingerprint,
    IReadOnlyList<string> DirectSourceIds,
    string InputVersionId,
    string? ParentResultId,
    string ComputedAt);

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? SourceId,
    string? RawRepresentation);

public sealed record RawQuantity(
    string Raw,
    double? Value,
    string? Unit,
    string CoordinateBasis,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasValue => Value.HasValue;
    public string Display => Raw;
}

public sealed record ClockSpec(
    string Id,
    string Unit,
    string CoordinateBasis,
    double Period,
    double? FirstTick,
    IReadOnlyList<ClockGap> Gaps,
    double TimeUncertainty);

public sealed record ClockGap(string GapId, double Start, double End, string Reason);

public sealed record WaveSample(
    int Index,
    RawQuantity Time,
    RawQuantity Voltage,
    bool IsClockHole,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record SignalSample(int Index, RawQuantity Time, bool Asserted, bool IsClockHole);

public sealed record RailDefinition(
    string RailId,
    string DisplayName,
    RawQuantity NominalVoltage,
    RawQuantity RisingThreshold,
    RawQuantity FallingThreshold,
    RawQuantity HysteresisBand,
    RawQuantity MinimumStableTime,
    RawQuantity MeasurementError,
    IReadOnlyList<WaveSample> Samples);

public sealed record SignalDefinition(
    string SignalId,
    string Kind,
    string? RailId,
    RawQuantity? MinimumStableTime,
    RawQuantity? MeasurementError,
    IReadOnlyList<SignalSample> Samples);

public sealed record DependencyEdge(
    string EdgeId,
    string From,
    string To,
    string Phase,
    RawQuantity MaxDelay,
    bool RequiredForStartup,
    bool RequiredForShutdown);

public sealed record DependencyGraph(
    string GraphId,
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ThresholdRevision(
    string ParameterVersionId,
    string ParentParameterVersionId,
    IReadOnlyList<RailThresholdOverride> RailOverrides,
    string Note);

public sealed record RailThresholdOverride(
    string RailId,
    RawQuantity? RisingThreshold,
    RawQuantity? FallingThreshold,
    RawQuantity? HysteresisBand,
    RawQuantity? MinimumStableTime);

public sealed record RawCapture(
    string SchemaVersion,
    string ProjectId,
    string ProjectName,
    string CaptureId,
    ClockSpec Clock,
    IReadOnlyList<RailDefinition> Rails,
    IReadOnlyList<SignalDefinition> Signals,
    DependencyGraph DependencyGraph,
    IReadOnlyList<ThresholdRevision> ThresholdRevisions,
    IReadOnlyList<CounterexampleSpec> Counterexamples,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CounterexampleSpec(
    string ExperimentId,
    string Kind,
    string Title,
    string Description,
    string BaselineVersionId,
    string DirectSourceKind,
    string DirectSourceId,
    JsonElement Change,
    IReadOnlyList<string> ExpectedFindingKinds);

public sealed record ProjectCreated(
    string ProjectId,
    string Name,
    string InitialVersionId,
    string CaptureId,
    RawCapture Raw);

public sealed record ThresholdForked(
    string ParameterVersionId,
    string ParentParameterVersionId,
    string RevisionId,
    string Note,
    IReadOnlyList<string> ChangedRailIds);

public sealed record SourceRevisionCommitted(
    string VersionId,
    string ParentVersionId,
    string SourceKind,
    string SourceId,
    string Reason,
    JsonElement RevisedSource);

public sealed record AnalysisRecorded(
    string Scope,
    string InputVersionId,
    string ParameterVersionId,
    IReadOnlyList<RailStateWindow> Windows,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<CycleCandidate> CycleCandidates,
    IReadOnlyList<GlitchCandidate> GlitchCandidates,
    IReadOnlyList<CounterexampleResult> CounterexampleResults,
    IReadOnlyList<Diagnostic> Diagnostics,
    AlgorithmProvenance Provenance);

public sealed record CounterexampleArchived(
    string ExperimentId,
    string Result,
    bool Adopted,
    string? RejectionReason,
    IReadOnlyList<string> AffectedResultIds,
    IReadOnlyList<string> UnaffectedResultIds,
    AlgorithmProvenance Provenance,
    MachineJudgement Judgement);

public sealed record ReviewDecisionRecorded(
    string DecisionId,
    string TargetResultId,
    string ExperimentId,
    string Reviewer,
    DecisionOutcome Outcome,
    string Comment,
    string? ParameterForkId,
    IReadOnlyList<string> ConflictWithDecisionIds);

public sealed record RailStateWindow(
    StableIdentity Identity,
    string RailId,
    string State,
    RawQuantity Start,
    RawQuantity End,
    RawQuantity Duration,
    bool MeetsMinimumStableTime,
    IReadOnlyList<string> EnteredByEventIds,
    AlgorithmProvenance Provenance);

public sealed record Finding(
    StableIdentity Identity,
    string Kind,
    string Severity,
    string Phase,
    string? RailId,
    string? EdgeId,
    string Title,
    string Detail,
    RawQuantity? ObservedValue,
    RawQuantity? RequiredValue,
    string? FirstViolationEventId,
    IReadOnlyList<string> EvidenceEventIds,
    CandidateStatus Status,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> AffectedObjectIds,
    AlgorithmProvenance Provenance)
{
    public bool IsCurrent => Status != CandidateStatus.Stale && Status != CandidateStatus.Unknown;
}

public sealed record CycleCandidate(
    StableIdentity Identity,
    IReadOnlyList<string> Edges,
    IReadOnlyList<string> Nodes,
    bool IsMinimal,
    IReadOnlyList<IReadOnlyList<string>> AlternativeMinimalEdgeSets,
    CandidateStatus Status,
    IReadOnlyList<string> RejectionReasons,
    AlgorithmProvenance Provenance);

public sealed record GlitchCandidate(
    StableIdentity Identity,
    string RailId,
    string Direction,
    RawQuantity Start,
    RawQuantity End,
    RawQuantity Duration,
    RawQuantity MinimumStableTime,
    bool ClassifiedAsGlitch,
    CandidateStatus Status,
    IReadOnlyList<string> RejectionReasons,
    AlgorithmProvenance Provenance);

public sealed record CounterexampleResult(
    string ExperimentId,
    string Kind,
    bool Adopted,
    string? RejectionReason,
    string DirectSourceId,
    IReadOnlyList<string> AffectedResultIds,
    IReadOnlyList<string> UnaffectedResultIds,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<CycleCandidate> CycleCandidates,
    IReadOnlyList<GlitchCandidate> GlitchCandidates,
    MachineJudgement Judgement,
    AlgorithmProvenance Provenance);

public sealed record MachineJudgement(
    string RuleId,
    string RuleVersion,
    string Decision,
    bool BoundedImpact,
    IReadOnlyList<MachineFact> Facts,
    IReadOnlyList<string> SourceEventIds);

public sealed record MachineFact(string Name, string Value, string UnitOrBasis);

public static class Quantity
{
    public static RawQuantity From(object? raw, string expectedUnit, string basis, string sourceId)
    {
        var text = raw?.ToString()?.Trim() ?? string.Empty;
        var diagnostics = new List<Diagnostic>();
        if (text.Length == 0)
        {
            diagnostics.Add(new("Q_EMPTY", Severity.Error, "数值为空，原始表示已保留。", sourceId, text));
            return new(text, null, null, basis, diagnostics);
        }

        var unit = ExtractUnit(text, expectedUnit);
        var numberText = text[..(text.Length - unit.Length)].Trim();
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            diagnostics.Add(new("Q_UNCONVERTIBLE", Severity.Error, $"无法转换为 {expectedUnit}；值保留在原位置。", sourceId, text));
            return new(text, null, unit, basis, diagnostics);
        }

        if (!UnitsCompatible(unit, expectedUnit))
        {
            diagnostics.Add(new("Q_UNIT_MISMATCH", Severity.Error, $"单位 {unit} 无法转换为 {expectedUnit}。", sourceId, text));
            return new(text, value, unit, basis, diagnostics);
        }

        if (unit != expectedUnit) value *= expectedUnit.ToLowerInvariant() switch
        {
            "v" when unit == "mV" => 0.001,
            "s" when unit == "ms" => 0.001,
            "s" when unit == "us" || unit == "µs" => 0.000001,
            _ => 1
        };

        if (value < 0) diagnostics.Add(new("Q_NEGATIVE", Severity.Warning, "物理量为负值，已保留并标记。", sourceId, text));
        return new(text, value, expectedUnit, basis, diagnostics);
    }

    public static RawQuantity Scalar(double value, string unit, string basis)
    {
        var raw = value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
        return new(raw, value, unit, basis, Array.Empty<Diagnostic>());
    }

    static string ExtractUnit(string text, string fallback)
    {
        foreach (var unit in new[] { "mV", "µs", "us", "ms", "V", "s" })
            if (text.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) return unit;
        return fallback;
    }

    static bool UnitsCompatible(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return true;
        return expected.ToLowerInvariant() switch
        {
            "v" => actual is "V" or "mV",
            "s" => actual is "s" or "ms" or "us" or "µs",
            _ => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        };
    }
}
