using System.Text.Json.Serialization;

namespace PowerSeqEvidenceRoom.Domain;

/// <summary>原始输入的不可变文档。提交后不覆盖，修订以新版本+父版本表达。</summary>
public sealed class InputDocument
{
    public required string CaseId { get; init; }
    public required string VersionId { get; init; }
    public string? ParentVersionId { get; init; }
    public string Note { get; init; } = "";
    public ClockSpec Clock { get; init; } = new();
    public List<RailSpec> Rails { get; init; } = new();
    public List<SignalSpec> Signals { get; init; } = new();
    public List<DependencyEdge> Edges { get; init; } = new();
    public List<Waveform> Waveforms { get; init; } = new();
    public List<MeasuredError> Measurements { get; init; } = new();
    /// <summary>逐行来源坐标：key=对象稳定身份，value=文件名:行号。</summary>
    public Dictionary<string, string> SourceCoords { get; init; } = new();
}

public sealed record ClockSpec
{
    /// <summary>原始采样率文本，例如 "1000Hz"。</summary>
    public string SampleRateRaw { get; init; } = "";
    public double? SampleRateHz { get; init; }
    /// <summary>坐标基准：时间轴原点（秒）。</summary>
    public double TimeOriginS { get; init; }
    /// <summary>被标记为时钟缺口的区间（保留原始 token 坐标）。</summary>
    public List<GapMarker> Gaps { get; init; } = new();
    public string? Diagnostic { get; init; }

    public double PeriodS => SampleRateHz is > 0 ? 1.0 / SampleRateHz.Value : 0;
}

public sealed record GapMarker(int StartIndex, int Count, string Raw, string Coord);

public sealed record RailSpec
{
    public required string RailId { get; init; }
    public required string Name { get; init; }
    /// <summary>上升阈值（进入 ON 的门限）。</summary>
    public required Quantity RiseThreshold { get; init; }
    /// <summary>下降阈值（掉出 ON 的门限）。二者可以不同。</summary>
    public required Quantity FallThreshold { get; init; }
    /// <summary>滞回半宽（以 V 规范化），围绕各自阈值。</summary>
    public required Quantity Hysteresis { get; init; }
    /// <summary>最短稳定时间：越界持续超过它才算违约。</summary>
    public required Quantity MinStable { get; init; }
    /// <summary>短于该时长的越界按 glitch 处理。</summary>
    public required Quantity GlitchMax { get; init; }
}

public sealed record SignalSpec
{
    public required string SignalId { get; init; }
    public required string Kind { get; init; } // enable | good
    public required string RailId { get; init; }
}

public sealed record DependencyEdge
{
    public required string EdgeId { get; init; }
    /// <summary>先上电/后掉电的轨。</summary>
    public required string FromRailId { get; init; }
    /// <summary>后上电（依赖 From）的轨。</summary>
    public required string ToRailId { get; init; }
    /// <summary>允许的最小/最大间隔；null 表示该侧不约束。</summary>
    public Quantity? MinDelay { get; init; }
    public Quantity? MaxDelay { get; init; }
    /// <summary>同时到达判定容差（小于等于视为同时）。</summary>
    public Quantity? SimulTolerance { get; init; }
}

public sealed record Waveform
{
    public required string WaveId { get; init; }
    public required string RailId { get; init; }
    public required string Phase { get; init; } // startup | shutdown
    public List<Sample> Samples { get; init; } = new();
}

/// <summary>采样点：保留原始电压文本；时间由采样时钟坐标推导，亦可显式覆盖。</summary>
public sealed record Sample
{
    public required int Index { get; init; }
    public required string RawVoltage { get; init; }
    public double? VoltageV { get; init; }
    public double TimeS { get; init; }
    /// <summary>该点是否落入声明的时钟缺口。</summary>
    public bool InClockGap { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed record MeasuredError
{
    public required string MeasurementId { get; init; }
    public required string RailId { get; init; }
    /// <summary>测量误差（±，以 V 规范化），参与越界判定形成 UNKNOWN 带。</summary>
    public required Quantity VoltageError { get; init; }
    public required Quantity TimingError { get; init; }
}
