using System.Text.Json.Serialization;

namespace PowerSeqEvidenceRoom.Domain;

public enum EventType
{
    ProjectCreated,
    InputCommitted,       // 原始输入提交（不可变）
    RevisionCommitted,    // 新版本输入（带父版本）
    Computed,             // 一次派生计算
    HumanAccepted,        // 采纳候选
    HumanRejected,        // 否定候选（带理由）
    ParameterAdjusted,    // 参数调整（分叉阈值版本）
    ConflictRecorded,     // 互相冲突的人工决定
    Undo,                 // 撤销
    Imported,             // 从档案导入
    Exported
}

/// <summary>
/// 机器可读取事件。判断依据以键值写在 Basis 里，例如：
/// riseThreshold/fallThreshold 不同；glitch-vs-violation 由 durationMs 决定；
/// cycle 给 minContradictingEdges。回放到某事件恢复当时依赖版本与结论。
/// </summary>
public sealed class AuditEvent
{
    public required string EventId { get; init; }
    public required EventType Type { get; init; }
    public required string SeqId { get; init; }          // 顺序号（稳定身份）
    public string? ParentEventId { get; init; }          // 链
    public required string VirtualTimeIso { get; init; }
    public required string InputVersionId { get; init; }
    public string? ReportId { get; init; }
    public string? TargetCandidateId { get; init; }
    public string? ParameterVersionId { get; init; }
    public string? ParentParameterVersionId { get; init; }
    public Dictionary<string, string> Basis { get; init; } = new();
    public List<string> AffectedObjectIds { get; init; } = new();
    public required string PayloadJson { get; init; }
    public string? UndoesEventId { get; init; }
    /// <summary>前一事件指纹，形成防改写链。</summary>
    public string ChainHash { get; set; } = "";
}

/// <summary>参数（阈值等）版本：可分叉，父子表达。</summary>
public sealed record ParameterVersion
{
    public required string ParameterVersionId { get; init; }
    public string? ParentParameterVersionId { get; init; }
    public required string BaseInputVersionId { get; init; }
    /// <summary>仅覆盖被分叉的阈值：railId -> (riseV?, fallV?)。</summary>
    public Dictionary<string, ThresholdOverride> Overrides { get; init; } = new();
    public required string Fingerprint { get; init; }
    public required string CreatedByEventId { get; init; }
}

public sealed record ThresholdOverride(double? RiseV, double? FallV, string? RiseRaw, string? FallRaw);
