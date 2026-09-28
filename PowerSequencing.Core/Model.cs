namespace PowerSequencing.Core;

public enum RailLevel { Low, High, Unknown }

public enum SegmentKind { StableLow, StableHigh }

public enum CandidateStatus { Proposed, Adopted, Rejected }

public enum VerdictKind { Pass, Violation, Ambiguous, Unknown }

/// <summary>Uncertainty interval for a measured crossing, in nanoseconds since t0.</summary>
public sealed record TimeRange(long EarliestNs, long LatestNs)
{
    public long MidNs => (EarliestNs + LatestNs) / 2;

    public bool Overlaps(TimeRange other, long windowNs) =>
        EarliestNs <= other.LatestNs + windowNs && other.EarliestNs <= LatestNs + windowNs;
}

public sealed record SamplePoint(long TimeNs, double MilliVolts);

public sealed record SignalPoint(long TimeNs, bool Asserted);

/// <summary>
/// Imported waveform. RawText keeps the original representation exactly as submitted;
/// normalized points are derived. Values that cannot be converted stay only in RawText
/// and produce diagnostics instead of being silently dropped.
/// </summary>
public sealed record Waveform(
    string WaveformId,
    string Subject,          // rail name or signal name
    string Kind,             // "rail" | "signal"
    string Unit,             // "mV" | "bool"
    string TimeBase,         // e.g. "ns-since-t0"
    double SampleClockHz,
    double MeasurementErrorMv,
    IReadOnlyList<SamplePoint> Points,
    IReadOnlyList<SignalPoint> SignalPoints,
    string RawText,
    string? ParentWaveformId,
    IReadOnlyList<string> Diagnostics);

/// <summary>Versioned timing constraints for one rail. Rise and fall use different thresholds.</summary>
public sealed record ThresholdParams(
    double RiseThresholdMv,
    double FallThresholdMv,
    double HysteresisMv,
    long MinStableNs,
    long SimultaneousWindowNs,
    long ShutdownMaxDelayNs);

public sealed record ThresholdVersion(
    string ThresholdVersionId,
    string Rail,
    int Version,
    string? ParentVersionId,
    ThresholdParams Params,
    string ParamsFingerprint,
    string Rationale);

public sealed record DependencyEdge(string From, string To, string Phase)
{
    public string Key => $"{From}->{To}@{Phase}";
}

/// <summary>Startup edges mean: From must be confirmed-on before To. Shutdown is checked in reverse.</summary>
public sealed record DependencyGraph(
    string GraphId,
    int Version,
    string? ParentGraphId,
    IReadOnlyList<DependencyEdge> Edges);

public sealed record ConfirmedTransition(
    RailLevel To,
    TimeRange Crossing,
    long ConfirmedAtNs);

public sealed record StateSegment(SegmentKind Kind, long StartNs, long EndNs);

public sealed record GlitchRecord(
    long StartNs,
    long EndNs,
    long DurationNs,
    double PeakMilliVolts,
    string Direction);   // "upward" | "downward"

public sealed record UnknownGap(long StartNs, long EndNs, string Reason);

public sealed record RailActivation(
    string Rail,
    IReadOnlyList<StateSegment> Segments,
    IReadOnlyList<ConfirmedTransition> Transitions,
    IReadOnlyList<GlitchRecord> Glitches,
    IReadOnlyList<UnknownGap> Gaps)
{
    public ConfirmedTransition? FirstOn =>
        Transitions.FirstOrDefault(t => t.To == RailLevel.High);

    public ConfirmedTransition? FirstOffAfterOn
    {
        get
        {
            var on = FirstOn;
            if (on is null) return null;
            return Transitions.FirstOrDefault(t => t.To == RailLevel.Low && t.ConfirmedAtNs >= on.ConfirmedAtNs);
        }
    }
}

public sealed record OrderingCandidate(
    string CandidateId,
    string RailA,
    string RailB,
    string Phase,                 // "startup" | "shutdown" | "good-after-on"
    VerdictKind Verdict,
    string Rationale,
    IReadOnlyList<string> Alternatives,   // all legal orders when not uniquely decidable
    CandidateStatus Status,
    string? RejectReason);

public sealed record CycleReport(
    bool HasCycle,
    IReadOnlyList<IReadOnlyList<DependencyEdge>> MinimalContradictionSets);

public sealed record FirstViolation(
    string Phase,
    string RailA,
    string RailB,
    long AtNs,
    string Detail);

/// <summary>Machine-readable judgment basis emitted by the analysis (glitch vs violation, dual thresholds, cycle sets).</summary>
public sealed record JudgmentRecord(
    string Type,
    IReadOnlyDictionary<string, object?> Data);

public sealed record AnalysisResult(
    string AnalysisId,
    string AlgorithmVersion,
    string ParamsFingerprint,
    IReadOnlyDictionary<string, string> DirectSources,  // slot -> stable id (waveform/threshold/graph)
    IReadOnlyList<RailActivation> Rails,
    IReadOnlyList<OrderingCandidate> Candidates,
    CycleReport Cycles,
    FirstViolation? FirstViolation,
    IReadOnlyList<JudgmentRecord> Judgments,
    IReadOnlyList<string> Diagnostics);

public sealed record DecisionRecord(
    string DecisionId,
    string CandidateId,
    string AnalysisId,
    string Outcome,        // "adopted" | "rejected"
    string Rationale,
    string ThresholdFingerprintAtDecision,
    string GraphIdAtDecision,
    bool Reverted);

public sealed record ConflictRecord(
    string ConflictId,
    string CandidateId,
    IReadOnlyList<string> DecisionIds,
    string Status,         // "open" | "resolved"
    string? Resolution,
    string? WinningDecisionId);
