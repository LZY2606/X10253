namespace PowerSeq.Domain;

public record Diagnostic(string Code, string Message, int? Line);

public record RawSample(double T, string TUnit, double V, string VUnit);

public record Sample(double TMs, double V);

/// <summary>
/// Parsed rail waveform. Raw representation is preserved verbatim; normalization
/// happens here at the derived layer. Unconvertible values stay in Raw with a diagnostic.
/// </summary>
public record RailWaveform(
    string Rail,
    IReadOnlyList<RawSample> Raw,
    IReadOnlyList<Sample> Normalized,
    IReadOnlyList<Diagnostic> Diagnostics,
    double? ClockPeriodMs,
    double MeasurementErrorMs);

/// <summary>
/// Import parser for the copyable small-sample waveform format:
///   # rail: VDD_CORE
///   # clock: 0.1 ms
///   # error: 0.02 ms
///   t_ms, v_V
///   0, 0.0
/// Format errors are explicit and carry line numbers.
/// </summary>
public static class WaveformParser
{
    public const string Kind = "waveform";

    public static RailWaveform Parse(string raw)
    {
        var diags = new List<Diagnostic>();
        var rawSamples = new List<RawSample>();
        string? rail = null;
        double? clockMs = null;
        double errMs = 0.0;
        string tUnit = "ms", vUnit = "V";
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var dataStarted = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                if (dataStarted)
                    throw new ImportException($"第 {lineNo} 行: 头部指令必须出现在数据之前");
                var body = line[1..].Trim();
                var colon = body.IndexOf(':');
                if (colon < 0) throw new ImportException($"第 {lineNo} 行: 无法识别的指令 '{body}'");
                var key = body[..colon].Trim().ToLowerInvariant();
                var val = body[(colon + 1)..].Trim();
                switch (key)
                {
                    case "rail": rail = val; break;
                    case "clock":
                        clockMs = ParseQuantity(val, lineNo, Units.TryToMs, "时钟周期");
                        break;
                    case "error":
                        errMs = ParseQuantity(val, lineNo, Units.TryToMs, "测量误差");
                        break;
                    default:
                        throw new ImportException($"第 {lineNo} 行: 未知指令 '{key}'");
                }
                continue;
            }
            if (!dataStarted)
            {
                // header row, e.g. "t_ms, v_V"
                var cols = line.Split(',');
                if (cols.Length != 2) throw new ImportException($"第 {lineNo} 行: 表头需要两列 't_单位, v_单位'");
                tUnit = ParseUnitSuffix(cols[0], "t", lineNo);
                vUnit = ParseUnitSuffix(cols[1], "v", lineNo);
                dataStarted = true;
                continue;
            }
            var parts = line.Split(',');
            if (parts.Length != 2)
                throw new ImportException($"第 {lineNo} 行: 数据行需要 '时间, 电压' 两列");
            if (!double.TryParse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var t))
                throw new ImportException($"第 {lineNo} 行: 时间 '{parts[0].Trim()}' 不是数字");
            if (!double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw new ImportException($"第 {lineNo} 行: 电压 '{parts[1].Trim()}' 不是数字");
            rawSamples.Add(new RawSample(t, tUnit, v, vUnit));
        }

        if (rail is null) throw new ImportException("缺少 '# rail: <名称>' 指令");
        if (!dataStarted) throw new ImportException("缺少表头行 't_单位, v_单位'");
        if (rawSamples.Count < 2) throw new ImportException("至少需要两个采样点");

        // Derived layer: normalize units. Unconvertible values stay raw + diagnostic.
        var norm = new List<Sample>();
        foreach (var s in rawSamples)
        {
            var okT = Units.TryToMs(s.T, s.TUnit, out var tms);
            var okV = Units.TryToVolts(s.V, s.VUnit, out var vv);
            if (!okT) diags.Add(new Diagnostic("unit.time.unknown", $"时间单位 '{s.TUnit}' 无法转换为 ms，该点保留原始表示", null));
            if (!okV) diags.Add(new Diagnostic("unit.voltage.unknown", $"电压单位 '{s.VUnit}' 无法转换为 V，该点保留原始表示", null));
            if (okT && okV) norm.Add(new Sample(tms, vv));
        }
        norm.Sort((a, b) => a.TMs.CompareTo(b.TMs));
        return new RailWaveform(rail, rawSamples, norm, diags, clockMs, errMs);
    }

    private static string ParseUnitSuffix(string col, string prefix, int lineNo)
    {
        var c = col.Trim();
        if (!c.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
            throw new ImportException($"第 {lineNo} 行: 表头列 '{c}' 应以 '{prefix}_<单位>' 命名");
        return c[(prefix.Length + 1)..].Trim();
    }

    private delegate bool Converter(double value, string unit, out double result);

    private static double ParseQuantity(string text, int lineNo, Converter conv, string what)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var v))
            throw new ImportException($"第 {lineNo} 行: {what}需要 '<数值> <单位>'，得到 '{text}'");
        if (!conv(v, parts[1], out var r))
            throw new ImportException($"第 {lineNo} 行: {what}单位 '{parts[1]}' 不受支持");
        return r;
    }
}

public class ImportException : Exception
{
    public ImportException(string message) : base(message) { }
}
