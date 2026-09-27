namespace PowerSeq.Core.Model;

public enum EventType
{
    ProjectCreated,
    CaptureCommitted,     // 原始输入提交（不可覆盖）
    ConstraintsCommitted, // 阈值版本提交（可分叉，带父版本）
    Analyzed,             // 产生报告（候选）
    Decided,              // 人工接受/否定/豁免
    ConflictResolved,     // 互相冲突的人工决定被解决
    Undone,               // 撤销（写补偿事件，不删历史）
    BranchArchived        // 反例档案条目落档
}

/// <summary>
/// 追加式事件。历史不可变；回到任意旧事件时恢复当时的版本与可见结论。
/// </summary>
public sealed record EventRecord(
    string EventId,          // 稳定身份（序号 + 前事件链 + 内容）
    long Seq,
    DateTimeOffset At,
    EventType Type,
    string Actor,
    string Summary,
    Json.JsonElement Payload,
    string? ParentEventId);

/// <summary>反例档案：被采纳与未采纳结果都保留。</summary>
public sealed record ArchiveEntry(
    string ArchiveId,
    string CaseId,
    string BranchKey,             // hysteresis | glitch | simultaneous | cycle | shutdown-reverse | clock-gap | threshold-stale
    string Title,
    string Perturbation,          // 单一直接来源改动描述
    string SourceRefJson,         // 改动的直接来源坐标
    string BaselineReportId,
    string PerturbedReportId,
    string? ConstraintForkId,
    string? CaptureForkId,
    bool Adopted,                 // 是否被采纳为有效反例
    string? AdoptedFindingId,
    IReadOnlyList<string> RejectedCandidateIds,
    IReadOnlyList<string> JudgmentEvents, // 机器可读判定事件 JSON
    DateTimeOffset CreatedAt,
    string EventId);
