using System.Globalization;
using System.Text;
using PowerSeqEvidenceRoom.Domain;

namespace PowerSeqEvidenceRoom.Import;

/// <summary>
/// 电源次序证据室 DSL (.pseq.txt)。
/// 面向领域的可读文本，可直接复制粘贴。解析保留原始 token、逐行坐标；
/// 单位无法转换只给警告并就地保留，不影响其它字段导入。
/// </summary>
public sealed class PSeqParser
{
    private readonly List<ParseDiagnostic> _diag = new();
    private readonly InputDocument _doc = new() { CaseId = "", VersionId = "" };
    private readonly Dictionary<string, RailSpec> _railIndex = new();
    private string _fileName = "input.pseq.txt";

    public ParseResult Parse(string text, string? fileName = null)
    {
        if (fileName is not null) _fileName = fileName;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Waveform? wave = null;
        SignalSpec? signal = null;
        var edges = new Dictionary<string, DependencyEdge>();

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var lineNo = i + 1;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kw = p[0].ToUpperInvariant();
            try
            {
                switch (kw)
                {
                    case "CASE":
                        Require(p, 2, lineNo);
                        _doc.CaseId = p[1];
                        Coord($"case:{p[1]}", lineNo);
                        break;
                    case "VERSION":
                        Require(p, 2, lineNo);
                        _doc.VersionId = p[1];
                        Coord($"version:{p[1]}", lineNo);
                        break;
                    case "PARENT":
                        Require(p, 2, lineNo);
                        _doc.ParentVersionId = p[1];
                        Coord($"version:{_doc.VersionId}", lineNo);
                        break;
                    case "NOTE":
                        _doc.Note = line.Length > 4 ? line[5..].Trim() : "";
                        break;
                    case "CLOCK":
                        ParseClock(p, line, lineNo);
                        break;
                    case "GAP":
                        ParseGap(p, lineNo);
                        break;
                    case "RAIL":
                        ParseRail(p, lineNo);
                        break;
                    case "SIGNAL":
                        signal = ParseSignal(p, lineNo);
                        break;
                    case "EDGE":
                        ParseEdge(p, lineNo, edges);
                        break;
                    case "WAVE":
                        wave = ParseWave(p, lineNo);
                        break;
                    case "S":
                        ParseSample(p, lineNo, wave);
                        break;
                    case "MEASURE":
                        ParseMeasure(p, lineNo);
                        break;
                    default:
                        Warn(1, "PSEQ101", $"未知关键字 '{kw}'，整行忽略", lineNo);
                        break;
                }
            }
            catch (ParseException pe)
            {
                Err(pe.Column, pe.Code, pe.Message, lineNo);
            }
        }

        if (string.IsNullOrWhiteSpace(_doc.CaseId))
            Err(1, "PSEQ001", "缺少 CASE 标识", lines.Length);
        if (string.IsNullOrWhiteSpace(_doc.VersionId))
            Err(1, "PSEQ002", "缺少 VERSION 标识", lines.Length);
        if (_doc.Rails.Count == 0)
            Err(1, "PSEQ003", "至少需要一条 RAIL", lines.Length);

        foreach (var wf in _doc.Waveforms)
        {
            if (!_railIndex.ContainsKey(wf.RailId))
                Err(1, "PSEQ004", $"WAVE {wf.WaveId} 引用了不存在的轨 {wf.RailId}", 0);
        }

        var sb = new StringBuilder();
        return new ParseResult
        {
            Document = _diag.Any(d => d.Severity == DiagSeverity.Error) ? null : _doc,
            Diagnostics = _diag
        };
    }

    private void ParseClock(string[] p, string line, int lineNo)
    {
        var m = SplitNameValues(line);
        var rateRaw = m.GetValueOrDefault("RATE", "");
        var hz = rateRaw.Length == 0 ? null : UnitConvert.ToHertz(rateRaw);
        var origin = 0.0;
        if (m.TryGetValue("ORIGIN", out var org))
            double.TryParse(org, NumberStyles.Float, CultureInfo.InvariantCulture, out origin);
        var clock = new ClockSpec
        {
            SampleRateRaw = rateRaw,
            SampleRateHz = hz,
            TimeOriginS = origin,
            Diagnostic = hz is null && rateRaw.Length > 0
                ? $"采样率 '{rateRaw}' 无法转换为 Hz，保留原表示" : null
        };
        _doc.Clock = clock;
        Coord("clock", lineNo);
        if (hz is null) Warn(12, "PSEQ010", clock.Diagnostic ?? "采样率无法转换", lineNo);
    }

    private void ParseGap(string[] p, int lineNo)
    {
        Require(p, 3, lineNo);
        if (!int.TryParse(p[1], out var start)) throw new ParseException(5, "PSEQ020", $"GAP 起始下标不是整数: {p[1]}");
        if (!int.TryParse(p[2], out var cnt) || cnt <= 0) throw new ParseException(5, "PSEQ020", $"GAP 长度必须为正整数: {p[2]}");
        var raw = string.Join(' ', p);
        var g = new GapMarker(start, cnt, raw, CoordStr(lineNo));
        _doc.Clock = _doc.Clock with { Gaps = _doc.Clock.Gaps.Append(g).ToList() };
        Coord($"gap:{start}", lineNo);
    }

    private void ParseRail(string[] p, int lineNo)
    {
        var m = SplitNameValues(string.Join(' ', p.Skip(1)));
        foreach (var need in new[] { "ID", "RISE", "FALL", "HYST", "STABLE", "GLITCH" })
            if (!m.ContainsKey(need))
                throw new ParseException(1, "PSEQ030", $"RAIL 缺少字段 {need}（格式: RAIL ID=.. NAME=.. RISE=.. FALL=.. HYST=.. STABLE=.. GLITCH=..）");

        var id = m["ID"];
        var riseV = UnitConvert.ToVolts(m["RISE"]);
        var fallV = UnitConvert.ToVolts(m["FALL"]);
        var hyst = UnitConvert.ToVolts(m["HYST"]);
        var stable = UnitConvert.ToSeconds(m["STABLE"]);
        var glitch = UnitConvert.ToSeconds(m["GLITCH"]);
        var rail = new RailSpec
        {
            RailId = id,
            Name = m.GetValueOrDefault("NAME", id),
            RiseThreshold = Quantity.Volts(m["RISE"], riseV),
            FallThreshold = Quantity.Volts(m["FALL"], fallV),
            Hysteresis = Quantity.Volts(m["HYST"], hyst),
            MinStable = Quantity.Seconds(m["STABLE"], stable),
            GlitchMax = Quantity.Seconds(m["GLITCH"], glitch)
        };
        if (riseV is null) Warn(1, "PSEQ031", rail.RiseThreshold.Diagnostic!, lineNo);
        if (fallV is null) Warn(1, "PSEQ031", rail.FallThreshold.Diagnostic!, lineNo);
        if (riseV.HasValue && fallV.HasValue && fallV.Value > riseV.Value)
            Warn(1, "PSEQ032", $"轨 {id} 下降阈值({fallV}V) 高于上升阈值({riseV}V)，请确认", lineNo);
        if (_railIndex.ContainsKey(id))
            throw new ParseException(6, "PSEQ033", $"轨标识重复: {id}");
        _railIndex[id] = rail;
        _doc.Rails.Add(rail);
        Coord($"rail:{id}", lineNo);
    }

    private SignalSpec ParseSignal(string[] p, int lineNo)
    {
        var m = SplitNameValues(string.Join(' ', p.Skip(1)));
        foreach (var need in new[] { "ID", "KIND", "RAIL" })
            if (!m.ContainsKey(need)) throw new ParseException(1, "PSEQ040", $"SIGNAL 缺少字段 {need}");
        if (m["KIND"] is not ("enable" or "good"))
            throw new ParseException(1, "PSEQ041", $"SIGNAL KIND 必须是 enable/good，实际 '{m["KIND"]}'");
        if (!_railIndex.ContainsKey(m["RAIL"]))
            Warn(1, "PSEQ042", $"SIGNAL {m["ID"]} 引用了不存在的轨 {m["RAIL"]}", lineNo);
        var sig = new SignalSpec { SignalId = m["ID"], Kind = m["KIND"], RailId = m["RAIL"] };
        _doc.Signals.Add(sig);
        Coord($"signal:{sig.SignalId}", lineNo);
        return sig;
    }

    private void ParseEdge(string[] p, int lineNo, Dictionary<string, DependencyEdge> edges)
    {
        var m = SplitNameValues(string.Join(' ', p.Skip(1)));
        foreach (var need in new[] { "ID", "FROM", "TO" })
            if (!m.ContainsKey(need)) throw new ParseException(1, "PSEQ050", $"EDGE 缺少字段 {need}");
        if (!_railIndex.ContainsKey(m["FROM"])) throw new ParseException(1, "PSEQ051", $"EDGE {m["ID"]} FROM 轨不存在: {m["FROM"]}");
        if (!_railIndex.ContainsKey(m["TO"])) throw new ParseException(1, "PSEQ051", $"EDGE {m["ID"]} TO 轨不存在: {m["TO"]}");
        var id = m["ID"];
        if (edges.ContainsKey(id)) throw new ParseException(6, "PSEQ052", $"EDGE 标识重复: {id}");
        Quantity? Q(string key) => m.TryGetValue(key, out var raw) ? Quantity.Seconds(raw, UnitConvert.ToSeconds(raw)) : null;
        var edge = new DependencyEdge
        {
            EdgeId = id,
            FromRailId = m["FROM"],
            ToRailId = m["TO"],
            MinDelay = Q("MIN"),
            MaxDelay = Q("MAX"),
            SimulTolerance = Q("SIMUL")
        };
        if (edge.MinDelay?.ValueBase is null && edge.MinDelay is not null) Warn(1, "PSEQ053", edge.MinDelay.Diagnostic!, lineNo);
        edges[id] = edge;
        _doc.Edges.Add(edge);
        Coord($"edge:{id}", lineNo);
    }

    private Waveform ParseWave(string[] p, int lineNo)
    {
        var m = SplitNameValues(string.Join(' ', p.Skip(1)));
        foreach (var need in new[] { "ID", "RAIL", "PHASE" })
            if (!m.ContainsKey(need)) throw new ParseException(1, "PSEQ060", $"WAVE 缺少字段 {need}");
        if (m["PHASE"] is not ("startup" or "shutdown"))
            throw new ParseException(1, "PSEQ061", $"WAVE PHASE 必须是 startup/shutdown，实际 '{m["PHASE"]}'");
        var wf = new Waveform { WaveId = m["ID"], RailId = m["RAIL"], Phase = m["PHASE"] };
        if (_doc.Waveforms.Any(w => w.WaveId == m["ID"]))
            throw new ParseException(6, "PSEQ062", $"WAVE 标识重复: {m["ID"]}");
        _doc.Waveforms.Add(wf);
        Coord($"wave:{wf.WaveId}", lineNo);
        return wf;
    }

    private void ParseSample(string[] p, int lineNo, Waveform? wave)
    {
        if (wave is null) throw new ParseException(1, "PSEQ070", "S 采样行必须出现在 WAVE 块之后");
        if (p.Length < 3) throw new ParseException(1, "PSEQ071", "S 行格式: S &lt;index&gt; &lt;rawVoltage&gt;");
        if (!int.TryParse(p[1], out var idx)) throw new ParseException(3, "PSEQ072", $"采样下标不是整数: {p[1]}");
        var rawV = p[2];
        var v = UnitConvert.ToVolts(rawV);
        var period = _doc.Clock.PeriodS;
        var inGap = _doc.Clock.Gaps.Any(g => idx >= g.StartIndex && idx < g.StartIndex + g.Count);
        var sample = new Sample
        {
            Index = idx,
            RawVoltage = rawV,
            VoltageV = v,
            TimeS = _doc.Clock.TimeOriginS + idx * period,
            InClockGap = inGap,
            Diagnostic = v is null ? $"采样电压 '{rawV}' 无法转换，保留原表示，不参与画图数值" : null
        };
        if (v is null) Warn(3, "PSEQ073", sample.Diagnostic!, lineNo);
        wave.Samples.Add(sample);
        Coord($"{wave.WaveId}#{idx}", lineNo);
    }

    private void ParseMeasure(string[] p, int lineNo)
    {
        var m = SplitNameValues(string.Join(' ', p.Skip(1)));
        foreach (var need in new[] { "ID", "RAIL", "VERR", "TERR" })
            if (!m.ContainsKey(need)) throw new ParseException(1, "PSEQ080", $"MEASURE 缺少字段 {need}");
        var verr = UnitConvert.ToVolts(m["VERR"]);
        var terr = UnitConvert.ToSeconds(m["TERR"]);
        var mes = new MeasuredError
        {
            MeasurementId = m["ID"],
            RailId = m["RAIL"],
            VoltageError = Quantity.Volts(m["VERR"], verr),
            TimingError = Quantity.Seconds(m["TERR"], terr)
        };
        if (verr is null) Warn(1, "PSEQ081", mes.VoltageError.Diagnostic!, lineNo);
        if (!_railIndex.ContainsKey(m["RAIL"])) Warn(1, "PSEQ082", $"MEASURE {m["ID"]} 引用了不存在的轨 {m["RAIL"]}", lineNo);
        _doc.Measurements.Add(mes);
        Coord($"measure:{mes.MeasurementId}", lineNo);
    }
