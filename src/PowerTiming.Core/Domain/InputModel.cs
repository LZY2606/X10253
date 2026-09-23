using System.Text.Json;

namespace PowerTiming.Core.Domain;

/// <summary>
/// A physical quantity as submitted. The raw representation is immutable evidence;
/// normalized SI value is only a derived convenience and may be absent.
/// </summary>
public sealed record RawQuantity(string Raw, string? Unit, string? CoordinateBasis, double? NormalizedSi, string? Diagnostic)
{
    public bool IsConvertible => NormalizedSi is not null;
}

public sealed record VoltageSample(
    int Index,
    RawQuantity Time,
    RawQuantity Voltage,
    RawQuantity? MeasurementError,
    IReadOnlyList<string> Flags,
    string StableSampleId);

public sealed record SignalEdge(
    string Signal,
    string Kind,
    RawQuantity At,
    string Source,
    string StableSignalId);

public sealed record RailInput(
    string RailId,
    string Name,
    RawQuantity Nominal,
    RawQuantity RiseThreshold,
    RawQuantity FallThreshold,
    RawQuantity? HysteresisBand,
    RawQuantity MinStable,
    IReadOnlyList<VoltageSample> Samples,
    IReadOnlyList<SignalEdge> Signals);

public sealed record DependencyEdge(
    string EdgeId,
    string FromRail,
    string ToRail,
    RawQuantity MinDelay,
    RawQuantity SimultaneousTolerance,
    string RuleVersion,
    IReadOnlyList<string> StartupOrder,
    IReadOnlyList<string> ShutdownOrder);

public sealed record ClockGap(RawQuantity Start, RawQuantity End, string Reason);

public sealed record ClockModel(
    RawQuantity NominalPeriod,
    RawQuantity Start,
    RawQuantity? TimeMeasurementError,
    RawQuantity? VoltageMeasurementError,
    IReadOnlyList<ClockGap> Gaps);

public sealed record HumanDecision(
    string DecisionId,
    string CandidateId,
    string Verdict,
    string Note,
    string Reviewer,
    RawQuantity DecidedAt);

public enum BundleKind
{
    Baseline,
    ParameterRevision,
    SourcePatch,
    Decision
}

/// <summary>
/// Parsed, immutable submission. Rails/edges absent in a revision mean
/// "inherit the parent value"; null on a baseline means a format diagnostic.
/// </summary>
public sealed record ImportBundle(
    string SchemaVersion,
    BundleKind Kind,
    string CaseId,
    string Title,
    string? ParentCommitId,
    string? Branch,
    string ChangeNote,
    ClockModel? Clock,
    IReadOnlyDictionary<string, RailInput> Rails,
    IReadOnlyDictionary<string, DependencyEdge> Edges,
    IReadOnlyList<HumanDecision> Decisions,
    JsonElement Original,
    string OriginalText,
    string ContentFingerprint);

public sealed record ImportDiagnostic(string Code, string Path, string Message, string? RawValue)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

public sealed record ImportResult(ImportBundle? Bundle, IReadOnlyList<ImportDiagnostic> Diagnostics)
{
    public bool IsValid => Bundle is not null && Diagnostics.All(d => !d.Code.StartsWith("E", StringComparison.Ordinal));
}
