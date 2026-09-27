using System.Text.Json.Serialization;

namespace PowerSeqEvidenceRoom.Domain;

public enum VerdictStatus
{
    Pass,
    Violation,
    Glitch,
    Unknown
}

public enum CandidateKind
{
    Glitch,
    Violation,
    Unknown,
    Simultaneous,
    CycleConflict,
    Rejected
}

/// <summary>
/// 候选是一等数据：被采纳与被拒绝都保留。
/// 每个候选带稳定身份、直接来源、算法版本与参数指纹。
/// </summary>
public sealed record VerdictCandidate
{
    public required string CandidateId { get; init; }
    public required CandidateKind Kind { get; init; }
    public required string Rule { get; init; }
    public required VerdictStatus Status { get; init; }
    public required bool Adopted { get; init; }
    public List<string> RejectionReasons { get; init; } = new();
    public required Provenance Provenance { get; init; }
    public Dictionary<string, string> Evidence { get; init; } = new();
    /// <summary>无法唯一决定时，一组彼此独立的合法候选共享同一个 ambiguityGroup。</summary>
    public string? AmbiguityGroup { get; init; }
    public List<string> MinContradictingEdges { get; init; } = new();
}

/// <summary>轨压状态窗口：状态由带滞回的状态机产生。</summary>
public sealed record StateWindow
{
    public required string WindowId { get; init; }
    public required string RailId { get; init; }
    public required string Phase { get; init; }
    public required string State { get; init; } // OFF RISING ON FALLING UNKNOWN
    public required double StartS { get; init; }
    public required double EndS { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public string? Note { get; init; }
    public Provenance? Provenance { get; init; }
}

/// <summary>越界片段：glitch 与违约由持续时间决定。</summary>
public sealed record Excursion
{
    public required string ExcursionId { get; init; }
    public required string RailId { get; init; }
    public required string Phase { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public required double DurationS { get; init; }
    public required string Direction { get; init; } // under over
    public required bool WithinErrorBand { get; init; }
    public required VerdictStatus Status { get; init; }
    public string? DecisionBasisEventId { get; init; }
}

public sealed record CrossingPoint
{
    public required string RailId { get; init; }
    public required string Phase { get; init; }
    public required string ThresholdKind { get; init; } // rise | fall
    public double? TimeS { get; init; }
    public int? Index { get; init; }
    public required bool CrossesGap { get; init; }
    public required bool WithinErrorBand { get; init; }
    public required Provenance Provenance { get; init; }
}

public sealed record Provenance
{
    /// <summary>稳定身份（导出/删库/导入后保持不变）。</summary>
    public required string StableId { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string ParamFingerprint { get; init; }
    /// <summary>直接来源（输入对象 id 列表，而不是传递来源）。</summary>
    public List<string> DirectSources { get; init; } = new();
    /// <summary>输入版本身份。</summary>
    public required string InputVersionId { get; init; }
    [JsonPropertyName("stale")] public bool Stale { get; set; }
    public string? StaleBecause { get; set; }
}

public sealed record AnalysisReport
{
    public required string ReportId { get; init; }
    public required string InputVersionId { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string ParamFingerprint { get; init; }
    public List<StateWindow> Windows { get; init; } = new();
    public List<Excursion> Excursions { get; init; } = new();
    public List<CrossingPoint> Crossings { get; init; } = new();
    public List<VerdictCandidate> Candidates { get; init; } = new();
    /// <summary>第一个违约证据（按时间序），页面默认定位。</summary>
    public VerdictCandidate? FirstViolation { get; init; }
    /// <summary>最小矛盾边集（循环依赖），每个合法集合都是独立候选。</summary>
    public List<List<string>> MinFeedbackEdgeSets { get; init; } = new();
    public List<string> Diagnostics { get; init; } = new();
}
