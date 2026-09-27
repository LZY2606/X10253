namespace PowerSeq.Core.Model;

public sealed record RailSpec(
    string Rail,
    Qty RiseThreshold,       // 上升阈值（原始表示）
    Qty FallThreshold,       // 下降阈值（原始表示，低于该值视为跌出窗口）
    Qty MinStable,           // 最小稳定时间：短于该时长的越界为 glitch
    Qty NominalVoltage,      // 标称轨压（画图/窗口用）
    Qty Tolerance);          // 窗口容差

public sealed record SignalSpec(string Rail, ColumnKind Kind);

/// <summary>依赖边：startup 时 Rail 必须在 DependsOn 之后；shutdown 自动取逆序要求。</summary>
public sealed record DepEdge(
    string Rail,
    string DependsOn,
    Qty MinDelay,         // 最小滞后（A 稳定到 B 开始上升）
    Qty MaxDelay,         // 最大滞后（超过为 SLOW）
    Qty SimulTolerance);  // 同时到达容差（含不确定性后仍交叠为 SIMULTANEOUS）

/// <summary>
/// 版本化时序约束文档（阈值版本）。内容不可变；修订以新版本 + 父版本关系表达。
/// </summary>
public sealed class ConstraintDoc
{
    public required string ConstraintId { get; init; }
    public required string Label { get; init; }
    public required int Version { get; init; }
    public required string? ParentId { get; init; }          // 父阈值版本
    public required string SourceCaptureId { get; init; }    // 适用的采集
    public required IReadOnlyList<RailSpec> Rails { get; init; }
    public required IReadOnlyList<SignalSpec> Signals { get; init; }
    public required IReadOnlyList<DepEdge> Edges { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }

    public RailSpec? Rail(string rail) => Rails.FirstOrDefault(r => r.Rail == rail);
    public DepEdge? Edge(string rail, string dependsOn) =>
        Edges.FirstOrDefault(e => e.Rail == rail && e.DependsOn == dependsOn);
}
