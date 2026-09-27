namespace PowerSeq.Core.Model;

/// <summary>半开时间区间窗口：start..end 为不确定性包络（秒，含测量/时钟误差）。</summary>
public sealed record Interval(double StartEarly, double StartLate, double EndEarly, double EndLate)
{
    public double StartMid => (StartEarly + StartLate) / 2;
    public double EndMid => (EndEarly + EndLate) / 2;
}

public enum RailState { Off, Entering, On, Falling, Unknown }

public sealed record RailSegment(string Rail, RailState State, double Start, double End, int FirstIndex, int LastIndex);

/// <summary>上升/下降越界事件。Kind = Glitch 或 Violation，由持续时间决定。</summary>
public enum ExcursionKind { RiseDip, FallPop }
public enum Disposition { Glitch, Violation }

public sealed record Excursion(
    string Rail,
    string Direction,          // "rise" | "fall"
    ExcursionKind Kind,
    Disposition Disposition,
    double DurationSeconds,
    double ThresholdVolts,
    double MinStableSeconds,
    int FirstIndex,
    int LastIndex,
    Interval Window,
    Derivation Derivation)
{
    public string StableId => $"exc:{Rail}:{Direction}:{FirstIndex}";
}

public sealed record Crossing(
    string Rail,
    string What,                // rail 名（电压跨阈值）或信号名
    string Direction,           // "rise" | "fall"
    Interval Window,
    int ThresholdIndex,
    Derivation Derivation)
{
    public string StableId => $"cross:{What}:{Direction}";
}

public enum FindingKind
{
    Order,        // 启动/关机依赖次序
    Cycle,        // 循环依赖
    ClockGap,     // 时钟缺口导致的未知
    Glitch        // 短暂越界（信息性，附带 glitch/violation 判定）
}

public enum FindingVerdict
{
    Satisfied,
    Simultaneous,
    Reversed,
    Slow,
    Violation,
    Cyclic,
    Unknown,
    GlitchObserved,
    ViolationObserved
}

public enum ReviewDecision { None, Accepted, Rejected, Waivered }
public enum Freshness { Current, Stale, Superseded }

/// <summary>
/// 判定发现：候选、拒绝原因、误差/未知均为一等数据。
/// RejectionReasons 给出未采纳候选的拒绝原因。Candidates 保留全部合法候选。
/// </summary>
public sealed record Finding(
    string FindingId,
    FindingKind Kind,
    string Subject,                          // 边 B->A、环 hash 等
    FindingVerdict Verdict,
    string Phase,                            // "startup" | "shutdown" | "global"
    Interval? EvidenceWindowA,
    Interval? EvidenceWindowB,
    double? ObservedGapSeconds,
    double? RequiredMinSeconds,
    double? RequiredMaxSeconds,
    double UncertaintySeconds,
    IReadOnlyList<string> Candidates,        // 无法唯一决定时的全部合法候选
    IReadOnlyList<string> RejectionReasons,  // 未采纳候选及原因
    IReadOnlyList<string> MinimalCycleEdges, // 最小矛盾边集
    IReadOnlyList<SourceRef> Evidence,
    Derivation Derivation,
    string Detail,
    ReviewDecision Decision = ReviewDecision.None,
    string? DecisionBy = null,
    string? DecisionRationale = null,
    string? DecisionEventId = null,
    Freshness Freshness = Freshness.Current,
    string? SupersededByFindingId = null)
{
    public bool IsViolation => Verdict is FindingVerdict.Reversed
        or FindingVerdict.Violation or FindingVerdict.ViolationObserved
        or FindingVerdict.Cyclic or FindingVerdict.Simultaneous or FindingVerdict.Slow;
}
