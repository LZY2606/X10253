using System.Globalization;
using PowerSeq.Core.Model;

namespace PowerSeq.Core.Import;

/// <summary>派生层：原始表示 -> 规范化（V / s）。无法转换的值留在原位置并附诊断。</summary>
public static class Normalizer
{
    public static NormCapture Normalize(RawCapture raw)
    {
        var diags = new List<string>();
        var timeFactor = TimeFactor(raw.TimeUnit, diags, "@time-unit");
        var clockErr = UnitTable.ToCanonicalTime(raw.ClockError)
                       ?? double.NaN;
        if (double.IsNaN(clockErr)) diags.Add($"@clock-error '{raw.ClockError.RawText}' 无法换算为秒，时钟不确定性按 0 处理");
        var periodSec = UnitTable.ToCanonicalTime(raw.NominalPeriod)
                        ?? double.NaN;
        if (double.IsNaN(periodSec)) diags.Add($"@period '{raw.NominalPeriod.RawText}' 无法换算为秒");

        var errors = new double[raw.Columns.Count];
        for (var c = 0; c < raw.Columns.Count; c++)
        {
            var col = raw.Columns[c];
            if (col.Kind == ColumnKind.Voltage)
            {
                errors[c] = VoltageOf(col.MeasurementError, col.Unit) ?? double.NaN;
                if (double.IsNaN(errors[c]))
                    diags.Add($"列 {col.HeaderText} 测量误差 '{col.MeasurementError.RawText}' 无法换算为 V，按 0 处理");
            }
            else errors[c] = 0;
        }

        var samples = new List<Sample>(raw.RowCount);
        var values = new double?[raw.Columns.Count];
        for (var r = 0; r < raw.RowCount; r++)
        {
            double? tSec = null;
            var tMissing = false;
            var tRaw = raw.TimeRaw[r];
            if (tRaw.Length == 0)
            {
                tMissing = true;
            }
            else if (double.TryParse(tRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var tVal))
            {
                tSec = tVal * timeFactor;
            }
            else
            {
                diags.Add($"第 {r + 1} 行时间 '{tRaw}' 无法解析，该拍时间未知");
                tMissing = true;
            }

            Array.Clear(values);
            for (var c = 0; c < raw.Columns.Count; c++)
            {
                var cell = raw.Cells[c][r];
                if (cell.Length == 0)
                {
                    values[c] = null; // 缺口
                    continue;
                }
                var q = Qty.Parse(cell);
                if (!q.IsConvertible)
                {
                    diags.Add($"第 {r + 1} 行列 {raw.Columns[c].HeaderText} 值 '{cell}' 无法解析，保留原位（视为未知）");
                    values[c] = null;
                    continue;
                }
                if (raw.Columns[c].Kind == ColumnKind.Voltage)
                {
                    var v = VoltageOf(q, raw.Columns[c].Unit);
                    if (v is null)
                    {
                        diags.Add($"第 {r + 1} 行列 {raw.Columns[c].Rail} 值 '{cell}' 与单位 {raw.Columns[c].Unit} 不兼容，保留原位");
                        values[c] = null;
                    }
                    else values[c] = v;
                }
                else
                {
                    values[c] = q.Value; // 信号 0/1（也接受电平原文）
                }
            }
            samples.Add(new Sample(raw.Index[r], tSec, tMissing, (double?[])values.Clone()));
        }

        return new NormCapture
        {
            Raw = raw,
            Samples = samples,
            ErrorVolts = errors,
            ClockErrorSeconds = double.IsNaN(clockErr) ? 0 : clockErr,
            NominalPeriodSeconds = double.IsNaN(periodSec) ? 0 : periodSec,
            Diagnostics = diags
        };
    }

    private static double? VoltageOf(Qty q, string columnUnit)
    {
        // 列单位优先；无单位裸数按列单位解释
        if (q.Value is null) return null;
        var unit = q.Unit ?? columnUnit;
        var asQty = q with { Unit = unit };
        var v = UnitTable.ToCanonicalVoltage(asQty);
        return v;
    }

    private static double TimeFactor(string unit, List<string> diags, string where)
    {
        var probe = Qty.Parse("1 " + unit);
        var f = UnitTable.ToCanonicalTime(probe);
        if (f is null)
        {
            diags.Add($"{where} 单位 '{unit}' 无法换算为秒，时间按数值原值处理");
            return 1.0;
        }
        return f.Value;
    }
}
