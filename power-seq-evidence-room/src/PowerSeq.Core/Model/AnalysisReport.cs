namespace PowerSeq.Core.Model;

public sealed record RailTimeline(
    string Rail,
    IReadOnlyList<RailSegment> Segments,
    Crossing? Rise,
    Crossing? Fall,
    IReadOnlyList<Excursion> Excursions);

public sealed record SignalTimeline(
    string Signal,
    ColumnKind Kind,
    Crossing? Rise,
    Crossing? Fall);

/// <summary>
/// 一份不可变分析报告，绑定 1 个采集版本 + 1 个阈值版本 + 1 个算法版本 + 参数指纹。
/// </summary>
public sealed class AnalysisReport
{
    public required string ReportId { get; init; }            // 内容寻址稳定身份
    public required string CaptureId { get; init; }
    public required string ConstraintId { get; init; }
    public required int ConstraintVersion { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string ParameterFingerprint { get; init; }
    public required IReadOnlyList<RailTimeline> Rails { get; init; }
    public required IReadOnlyList<SignalTimeline> Signals { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<Interval> ClockGaps { get; init; }
    public required Finding? FirstViolation { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
