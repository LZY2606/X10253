namespace PowerSeq.Core.Model;

public enum ColumnKind { Voltage, Enable, Good }

/// <summary>原始列定义：导入时保留原始表示（含单位、误差），派生层才规范化。</summary>
public sealed record RawColumn(
    string Rail,
    ColumnKind Kind,
    string Unit,
    Qty MeasurementError,
    string HeaderText);

/// <summary>
/// 不可变原始采集。Rows 中的每个单元格为原始文本；空字符串表示该拍缺失（时钟缺口）。
/// IndexColumn 为采样拍序号（始终完整），TimeColumn 为带单位的采样时刻（可选）。
/// </summary>
public sealed class RawCapture
{
    public required string CaptureId { get; init; }
    public required string Label { get; init; }
    public required string TimeUnit { get; init; }          // 时间列单位基准，如 us
    public required Qty ClockError { get; init; }           // 采样时钟误差，带原始表示
    public required Qty NominalPeriod { get; init; }        // 名义采样周期
    public required IReadOnlyList<RawColumn> Columns { get; init; }
    public required IReadOnlyList<int> Index { get; init; }
    /// <summary>时间列原始文本（与 Index 等长）。</summary>
    public required IReadOnlyList<string> TimeRaw { get; init; }
    /// <summary>cells[columnOrdinal][rowOrdinal] 为原始文本，缺失拍为 ""。</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Cells { get; init; }

    public int RowCount => Index.Count;
}

/// <summary>规范化后的单拍采样。Missing 表示时钟缺口（该拍无观测）。</summary>
public sealed record Sample(int Index, double? TimeSeconds, bool TimeMissing, IReadOnlyList<double?> Values)
{
    public bool Missing(int col) => Values[col] is null;
}

/// <summary>规范化采集：电压统一为 V，时间统一为 s；无法转换的单元保留诊断。</summary>
public sealed class NormCapture
{
    public required RawCapture Raw { get; init; }
    public required IReadOnlyList<Sample> Samples { get; init; }
    public required IReadOnlyList<double> ErrorVolts { get; init; } // 每列测量误差(V)，无法转换为 NaN
    public required double ClockErrorSeconds { get; init; }          // 无法转换为 NaN
    public required double NominalPeriodSeconds { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }

    public IEnumerable<(int col, string rail, ColumnKind kind)> VoltageColumns()
    {
        for (var i = 0; i < Raw.Columns.Count; i++)
            if (Raw.Columns[i].Kind == ColumnKind.Voltage)
                yield return (i, Raw.Columns[i].Rail, ColumnKind.Voltage);
    }

    public IEnumerable<(int col, string rail, ColumnKind kind)> SignalColumns()
    {
        for (var i = 0; i < Raw.Columns.Count; i++)
            if (Raw.Columns[i].Kind != ColumnKind.Voltage)
                yield return (i, Raw.Columns[i].Rail, Raw.Columns[i].Kind);
    }
}
