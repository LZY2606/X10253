using System.Globalization;
using System.Text.RegularExpressions;
using PowerSeq.Core.Model;

namespace PowerSeq.Core.Import;

/// <summary>
/// 小型可复制样例文本格式：
///
///   @case cap-demo 演示板启动采集
///   @time-unit us
///   @clock-error 200 ns
///   @period 100 us
///   # idx, t, 3V3(V ±20mV), 1V8(V ±10mV), EN_3V3, PG_3V3
///   0, 0,   0.00, 0.00, 0, 0
///   ...
///
/// 空单元格表示该拍缺失（时钟缺口）。原始文本逐格保留。
/// </summary>
public static class CaptureParser
{
    private static readonly Regex HeaderCol =
        new(@"^(?<rail>[A-Za-z_][\w.\-]*)\s*(?:\((?<meta>[^)]*)\))?$", RegexOptions.Compiled);

    public static RawCapture Parse(string text, string? captureIdOverride = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? captureId = captureIdOverride, label = null, timeUnit = null;
        Qty? clockError = null, period = null;
        string[]? headerCells = null;
        var headerLine = -1;
        var dataRows = new List<(int lineNo, string[] cells)>();

        for (var li = 0; li < lines.Length; li++)
        {
            var rawLine = lines[li];
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("@case", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line[5..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0].Length == 0)
                    throw new ImportFormatException("@case 需要 <id> [标签]", li + 1);
                captureId ??= parts[0].Trim();
                label = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
            }
            else if (line.StartsWith("@time-unit", StringComparison.OrdinalIgnoreCase))
            {
                timeUnit = line["@time-unit".Length..].Trim();
                if (timeUnit.Length == 0) throw new ImportFormatException("@time-unit 缺少单位", li + 1);
            }
            else if (line.StartsWith("@clock-error", StringComparison.OrdinalIgnoreCase))
            {
                var q = Qty.Parse(line["@clock-error".Length..].Trim());
                if (!q.IsConvertible) throw new ImportFormatException("@clock-error 无法解析为量", li + 1);
                clockError = q;
            }
            else if (line.StartsWith("@period", StringComparison.OrdinalIgnoreCase))
            {
                var q = Qty.Parse(line["@period".Length..].Trim());
                if (!q.IsConvertible) throw new ImportFormatException("@period 无法解析为量", li + 1);
                period = q;
            }
            else if (line.StartsWith('#'))
            {
                if (headerCells is not null) throw new ImportFormatException("重复的列头", li + 1);
                headerCells = SplitCsv(line[1..]);
                headerLine = li + 1;
            }
            else
            {
                dataRows.Add((li + 1, SplitCsv(rawLine)));
            }
        }

        if (captureId is null) throw new ImportFormatException("缺少 @case <id>", 1);
        if (timeUnit is null) throw new ImportFormatException("缺少 @time-unit", 1);
        if (clockError is null) throw new ImportFormatException("缺少 @clock-error", 1);
        if (period is null) throw new ImportFormatException("缺少 @period", 1);
        if (headerCells is null) throw new ImportFormatException("缺少 '# idx, t, ...' 列头", 1);
        if (headerCells.Length < 3) throw new ImportFormatException("至少需要 idx, t 与一个信号列", headerLine);

        if (!headerCells[0].Trim().Equals("idx", StringComparison.OrdinalIgnoreCase))
            throw new ImportFormatException("第一列必须是 idx", headerLine, 1);
        if (!headerCells[1].Trim().Equals("t", StringComparison.OrdinalIgnoreCase))
            throw new ImportFormatException("第二列必须是 t", headerLine, 2);

        var columns = new List<RawColumn>();
        for (var c = 2; c < headerCells.Length; c++)
        {
            var cell = headerCells[c].Trim();
            var m = HeaderCol.Match(cell);
            if (!m.Success) throw new ImportFormatException($"列头 '{cell}' 无法解析（期望 name(V ±err) 或 name）", headerLine, c + 1);
            var rail = m.Groups["rail"].Value;
            var meta = m.Groups["meta"].Success ? m.Groups["meta"].Value.Trim() : null;
            if (meta is null)
            {
                columns.Add(new RawColumn(rail, GuessSignal(rail), "1", new Qty("0", 0.0, null), cell));
                continue;
            }

            // meta 形如 "V ±20mV" / "mV ±5" / "1 ±0"（信号列）
            var mp = Regex.Match(meta, @"^(?<unit>[^\s±]+)\s*(?:[±+/-]\s*(?<err>.+))?$");
            if (!mp.Success) throw new ImportFormatException($"列头元数据 '{meta}' 无法解析", headerLine, c + 1);
            var unit = mp.Groups["unit"].Value;
            var errText = mp.Groups["err"].Success ? mp.Groups["err"].Value.Trim() : "0";
            var errQty = Qty.Parse(errText);
            if (!errQty.IsConvertible) throw new ImportFormatException($"测量误差 '{errText}' 无法解析", headerLine, c + 1);

            var kind = UnitTable.IsVoltageUnit(unit)
                ? ColumnKind.Voltage
                : unit is "1" or "logic" or "" ? GuessSignal(rail) : GuessSignal(rail);
            columns.Add(new RawColumn(rail, kind, unit, errQty, cell));
        }

        var idx = new List<int>();
        var times = new List<string>();
        var cells = Enumerable.Range(0, columns.Count).Select(_ => new List<string>()).ToList();
        int? prevIdx = null;
        foreach (var (lineNo, rowCells) in dataRows)
        {
            if (rowCells.Length != headerCells!.Length)
                throw new ImportFormatException($"列数 {rowCells.Length} 与列头 {headerCells.Length} 不一致", lineNo);
            if (!int.TryParse(rowCells[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                throw new ImportFormatException($"idx '{rowCells[0]}' 不是整数", lineNo, 1);
            if (prevIdx is not null && i <= prevIdx.Value)
                throw new ImportFormatException($"idx 必须严格递增（{prevIdx} -> {i}）", lineNo, 1);
            prevIdx = i;
            idx.Add(i);
            times.Add(rowCells[1].Trim());
            for (var c = 0; c < columns.Count; c++)
                cells[c].Add(rowCells[c + 2].Trim());
        }

        if (idx.Count == 0) throw new ImportFormatException("没有数据行", headerLine);

        return new RawCapture
        {
            CaptureId = captureId,
            Label = label ?? captureId,
            TimeUnit = timeUnit,
            ClockError = clockError,
            NominalPeriod = period,
            Columns = columns,
            Index = idx,
            TimeRaw = times,
            Cells = cells.Select(l => (IReadOnlyList<string>)l).ToList()
        };
    }

    private static ColumnKind GuessSignal(string rail) =>
        rail.StartsWith("PG", StringComparison.OrdinalIgnoreCase) ||
        rail.Contains("PG_", StringComparison.OrdinalIgnoreCase) ||
        rail.Contains("_PG", StringComparison.OrdinalIgnoreCase)
            ? ColumnKind.Good
            : ColumnKind.Enable;

    private static string[] SplitCsv(string line) =>
        line.Split(',').Select(s => s.Trim()).ToArray();
}
