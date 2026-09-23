namespace PowerSequencer.Domain;

/// <summary>原始采样点：导入层保留字符串原表示，不做解析。</summary>
public record RawSample(string T, string V);

/// <summary>单轨原始波形输入（不可覆盖，修订以新版本+父版本表达）。</summary>
public record RailSeries(string Rail, string TimeUnit, string ValueUnit, List<RawSample> Samples);

/// <summary>版本化阈值：上升/下降使用不同阈值，带滞回与最小稳定时间。</summary>
public record RailThreshold(string Rail, double RiseV, double FallV, double HysteresisV, double MinStableMs, string Unit);

public record ThresholdSet(List<RailThreshold> Rails);

/// <summary>依赖边：From 轨 good 之后，To 轨才允许抬升，最小间隔 MinDelayMs。</summary>
public record DependencyEdge(string From, string To, double MinDelayMs);

public record DependencyGraph(List<DependencyEdge> Edges);

/// <summary>采样时钟：周期、测量误差（±ms）与显式时钟缺口。</summary>
public record ClockSpec(double PeriodMs, double ErrorMs, List<ClockGap> Gaps);

public record ClockGap(double StartMs, double EndMs);

/// <summary>规范化后的采样点；无法转换的原始点留在 Diagnostics。</summary>
public record NormPoint(double TMs, double V);

public record NormSeries(string Rail, List<NormPoint> Points, List<string> Diagnostics);

public enum RailState { Low, High, Unknown }

public record StateWindow(string Rail, double FromMs, double ToMs, RailState State);

public record GlitchEvent(string Rail, double AtMs, double DurationMs, string Direction, string Basis);

public record Violation(
    string Kind,
    string Summary,
    List<string> Involved,
    double? EvidenceAtMs,
    string Basis);

public record SequenceCandidate(List<string> Order, string Status, string? RejectReason);

public record CycleContradiction(List<DependencyEdge> MinimalEdgeSet, List<List<DependencyEdge>> AllMinimalSets);

public record EvalInput(
    List<RailSeries> RawSeries,
    ThresholdSet Thresholds,
    DependencyGraph Graph,
    ClockSpec Clock);

public record EvalOutput(
    List<NormSeries> Normalized,
    List<StateWindow> Windows,
    List<GlitchEvent> Glitches,
    List<Violation> Violations,
    List<SequenceCandidate> StartupCandidates,
    List<SequenceCandidate> ShutdownCandidates,
    List<CycleContradiction> Cycles,
    List<string> Unknowns);
