using System.Globalization;
using System.Text;

namespace PowerSequencing.Core;

/// <summary>
/// Importer for the "power sequencing evidence room" text DSL.
/// Every accepted value keeps its raw token, unit and 1-based source line.
/// Malformed input produces typed diagnostics with line numbers instead of
/// throwing, so callers can show copy-pasteable format errors.
/// </summary>
public sealed partial class CaseParser
{
    private readonly List<RailDef> _rails = [];
    private readonly List<SignalDef> _signals = [];
    private readonly List<EdgeEvent> _edges = [];
    private readonly List<DependencyEdge> _deps = [];
    private readonly List<SamplePoint> _samples = [];
    private readonly List<PhaseWindow> _phases = [];
    private readonly List<GapAnnotation> _gaps = [];
    private readonly List<ImportDiagnostic> _errors = [];
    private readonly List<ImportDiagnostic> _warnings = [];
    private string? _name;
    private RawTime? _clock;

    public static CaseDoc ParseCase(string text)
    {
        var p = new CaseParser();
        p.Run(text);
        p.CrossValidate();
        return new CaseDoc(p._name, p._clock, p._rails, p._signals, p._edges, p._deps,
            p._samples, p._phases, p._gaps, p._errors, p._warnings, text.Replace("\r\n", "\n"));
    }

    public static (RawQuantity value, RawQuantity? unc) ParseReadingPublic(string token) => ParseReading(token);

    private void Run(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var raw = lines[i];
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            try { Directive(line, lineNo, raw); }
            catch (ParseDslException ex)
            {
                _errors.Add(new ImportDiagnostic(lineNo, ex.Code, ex.Message, raw.TrimEnd()));
            }
        }
    }

    private static string StripComment(string line)
    {
        var inStr = false;
        var sb = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inStr = !inStr;
            if (c == '#' && !inStr) break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void Directive(string line, int lineNo, string raw)
    {
        var sp = SplitFirst(line);
        var kw = sp.Head;
        var rest = sp.Rest.Trim();
        switch (kw.ToLowerInvariant())
        {
            case "case": _name = RequireText(rest, lineNo); break;
            case "clock": ParseClock(rest, lineNo); break;
            case "rail": ParseRail(rest, lineNo, raw); break;
            case "signal": ParseSignal(rest, lineNo, raw); break;
            case "edge": ParseEdge(rest, lineNo, raw); break;
            case "dep": ParseDep(rest, lineNo, raw); break;
            case "sample": ParseSample(rest, lineNo, raw); break;
            case "phase": ParsePhase(rest, lineNo, raw); break;
            case "gap": ParseGap(rest, lineNo, raw); break;
            default: throw new ParseDslException("E_UNKNOWN_KEYWORD",
                $"unknown directive '{kw}' (expected case|clock|rail|signal|edge|dep|sample|phase|gap)");
        }
    }

    private void ParseClock(string rest, int lineNo)
    {
        var p = SplitFirst(rest);
        if (!p.Head.Equals("period", StringComparison.OrdinalIgnoreCase))
            throw new ParseDslException("E_CLOCK", $"clock expects 'period <time>', got '{rest}'");
        var t = TimeParser.Parse(p.Rest);
        if (!t.IsValid) throw new ParseDslException("E_TIME", t.Error!);
        if (_clock is not null) Warn(lineNo, "W_DUPLICATE_CLOCK", "duplicate clock period; the last one is used");
        _clock = t;
    }

    private void ParseRail(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var name = p.Head;
        RequireIdent(name, "rail name");
        var f = Fields(p.Rest);
        string? label = null; RawQuantity rise = null!, fall = null!; RawTime minStable = null!, glitch = null!;
        foreach (var (key, val) in f)
        {
            switch (key.ToLowerInvariant())
            {
                case "label": label = val; break;
                case "rise_at":
                    rise = QuantityParser.Parse(val);
                    if (!rise.IsConvertible) throw new ParseDslException("E_UNIT", rise.Error!);
                    break;
                case "fall_below":
                    fall = QuantityParser.Parse(val);
                    if (!fall.IsConvertible) throw new ParseDslException("E_UNIT", fall.Error!);
                    break;
                case "min_stable":
                    minStable = TimeParser.Parse(val);
                    if (!minStable.IsValid) throw new ParseDslException("E_TIME", minStable.Error!);
                    break;
                case "glitch_max":
                    glitch = TimeParser.Parse(val);
                    if (!glitch.IsValid) throw new ParseDslException("E_TIME", glitch.Error!);
                    break;
                default: throw new ParseDslException("E_RAIL_FIELD",
                    $"unknown rail field '{key}' (expected label|rise_at|fall_below|min_stable|glitch_max)");
            }
        }
        if (rise is null || fall is null || minStable is null || glitch is null)
            throw new ParseDslException("E_RAIL_REQUIRED",
                "rail requires rise_at, fall_below, min_stable and glitch_max");
        if (_rails.Any(r => r.Name == name))
            throw new ParseDslException("E_DUP_RAIL", $"rail '{name}' is already defined");
        _rails.Add(new RailDef(name, label, rise, fall, minStable, glitch, lineNo, raw.TrimEnd()));
    }
