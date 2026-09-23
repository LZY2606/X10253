using System.Text.Json;

namespace PowerTiming.Core.Domain;

public enum EventType
{
    ProjectCreated,
    BundleSubmitted,
    DerivationRecorded,
    CandidateRaised,
    CandidateRuled,
    BranchCreated,
    ReviewOpened
}

public sealed record HistoryEvent(
    string EventId,
    long Sequence,
    EventType Type,
    string Branch,
    string? CommitId,
    JsonElement Payload,
    string PayloadFingerprint,
    string AlgorithmVersion,
    DateTimeOffset RecordedAt,
    string? Actor);

public sealed record CommitNode(
    string CommitId,
    BundleKind Kind,
    string Title,
    string ChangeNote,
    string? ParentCommitId,
    string Branch,
    string RawBundleFingerprint,
    DateTimeOffset RecordedAt,
    int Depth);

public enum DerivationKind
{
    RailWindow,
    SignalCheck,
    EdgeCheck,
    CycleAnalysis,
    FirstViolation
}

public enum DerivationStatus
{
    Current,
    Stale,
    Superseded
}

public sealed record DirectSourceRef(string Kind, string Id, string Role);

public sealed record DerivedObject(
    string ResultId,
    DerivationKind Kind,
    string Branch,
    string CommitId,
    string AlgorithmVersion,
    string ParameterFingerprint,
    IReadOnlyList<DirectSourceRef> DirectSources,
    JsonElement Result,
    DerivationStatus Status,
    string? StaleReason,
    string? SupersededByResultId,
    string EventId,
    DateTimeOffset RecordedAt);

public enum CandidateStatus
{
    Open,
    Accepted,
    Rejected,
    Conflicted
}

public sealed record Candidate(
    string CandidateId,
    string CommitId,
    string Branch,
    string Category,
    string Title,
    JsonElement Detail,
    string FirstResultId,
    IReadOnlyList<string> DirectSourceIds,
    CandidateStatus Status,
    string? CurrentRulingEventId,
    DateTimeOffset CreatedAt);

public sealed record Ruling(
    string RulingId,
    string CandidateId,
    string CommitId,
    string Branch,
    string Verdict,
    string Note,
    string Reviewer,
    DateTimeOffset DecidedAt,
    string EventId);

public sealed record ProjectInfo(string ProjectId, string Name, DateTimeOffset CreatedAt, string CreatedFromCaseId);
