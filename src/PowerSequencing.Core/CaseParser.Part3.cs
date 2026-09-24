using System.Globalization;

namespace PowerSequencing.Core;

public sealed partial class CaseParser
{
    private sealed record Pair(string Head, string Rest);

    private void ParseSample(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        if (!int.TryParse(p.Head, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) || idx < 0)
            throw new ParseDslException("E_SAMPLE_INDEX", "sample index must be a non-negative integer");
        RawTime? at = null;
        var readings = new List<RailReading>();
        var logic = new List<LogicReading>();
        foreach (var (key, val) in Fields(p.Rest))
        {
            if (key.Equals("at", StringComparison.OrdinalIgnoreCase))
            {
                at = TimeParser.Parse(val);
                if (!at.IsValid) throw new ParseDslException("E_TIME", at.Error!);
            }
            else if (key.EndsWith('!'))
            {
                var sig = key[..^1];
                RequireIdent(sig, "signal name");
                var b = val.ToLowerInvariant() switch
                {
                    "1" or "h" or "high" => true,
                    "0" or "l" or "low" => false,
                    _ => throw new ParseDslException("E_LOGIC", $"signal level '{val}' must be high/1 or low/0")
                };
                logic.Add(new LogicReading(sig, b, val, lineNo));
            }
            else
            {
                RequireIdent(key, "rail name");
                var parsed = ParseReading(val);
                readings.Add(new RailReading(key, parsed.value, parsed.unc, lineNo, parsed.token));
                if (parsed.value.Error is not null)
                    Warn(lineNo, "W_UNCONVERTIBLE",
                        $"sample {idx} rail '{key}': {parsed.value.Error}; raw value retained in place");
            }
        }
        if (at is null) throw new ParseDslException("E_SAMPLE_TIME", "sample requires at <time>");
        if (_samples.Any(s => s.Index == idx))
            throw new ParseDslException("E_DUP_SAMPLE", $"sample index {idx} is already used");
        _samples.Add(new SamplePoint(idx, at, readings, logic, lineNo, raw.TrimEnd()));
    }

    private static (RawQuantity value, RawQuantity? unc, string token) ParseReading(string token)
    {
        var pm = token.IndexOf('\u00b1');
        if (pm >= 0)
        {
            var v = QuantityParser.Parse(token[..pm].Trim());
            var u = QuantityParser.Parse(token[(pm + 1)..].Trim());
            return (v, u.IsConvertible ? u : RawQuantity.Unparseable(u.Raw, u.Error!), token.Trim());
        }
        return (QuantityParser.Parse(token), null, token.Trim());
    }

    private void ParsePhase(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var name = p.Head;
        RequireIdent(name, "phase name");
        RawTime? from = null, to = null;
        foreach (var (key, val) in Fields(p.Rest))
        {
            switch (key.ToLowerInvariant())
            {
                case "from":
                    from = TimeParser.Parse(val);
                    if (!from.IsValid) throw new ParseDslException("E_TIME", from.Error!);
                    break;
                case "to":
                    to = TimeParser.Parse(val);
                    if (!to.IsValid) throw new ParseDslException("E_TIME", to.Error!);
                    break;
                default: throw new ParseDslException("E_PHASE_FIELD", $"unknown phase field '{key}' (expected from|to)");
            }
        }
        if (from is null || to is null)
            throw new ParseDslException("E_PHASE_TIME", "phase requires from <time> and to <time>");
        if (from.TotalMs > to.TotalMs)
            throw new ParseDslException("E_PHASE_RANGE", "phase from must be <= to");
        _phases.Add(new PhaseWindow(name, from, to, lineNo, raw.TrimEnd()));
    }

    private void ParseGap(string rest, int lineNo, string raw)
    {
        RawTime? from = null, to = null; string? reason = null;
        foreach (var (key, val) in Fields(rest))
        {
            switch (key.ToLowerInvariant())
            {
                case "from":
                    from = TimeParser.Parse(val);
                    if (from is null || !from.IsValid) throw new ParseDslException("E_TIME", from?.Error ?? "missing from");
                    break;
                case "to":
                    to = TimeParser.Parse(val);
                    if (to is null || !to.IsValid) throw new ParseDslException("E_TIME", to.Error!);
                    break;
                case "reason": reason = val; break;
                default: throw new ParseDslException("E_GAP_FIELD", $"unknown gap field '{key}' (expected from|to|reason)");
            }
        }
        if (from is null || to is null)
            throw new ParseDslException("E_GAP_TIME", "gap requires from <time> and to <time>");
        if (from.TotalMs >= to.TotalMs)
            throw new ParseDslException("E_GAP_RANGE", "gap from must be < to");
        _gaps.Add(new GapAnnotation(from, to, reason, lineNo, raw.TrimEnd()));
    }

    private void CrossValidate()
    {
        if (_clock is null)
            _errors.Add(new ImportDiagnostic(0, "E_NO_CLOCK", "a 'clock period' declaration is required", string.Empty));
        if (_rails.Count == 0)
            _errors.Add(new ImportDiagnostic(0, "E_NO_RAILS", "at least one rail must be declared", string.Empty));
        foreach (var d in _deps)
        {
            if (!_rails.Any(r => r.Name == d.From))
                _errors.Add(new ImportDiagnostic(d.SourceLine, "E_UNKNOWN_RAIL",
                    $"dependency source rail '{d.From}' is not declared", d.RawLine));
            if (!_rails.Any(r => r.Name == d.To))
                _errors.Add(new ImportDiagnostic(d.SourceLine, "E_UNKNOWN_RAIL",
                    $"dependency target rail '{d.To}' is not declared", d.RawLine));
        }
        foreach (var e in _edges)
            if (!_signals.Any(s => s.Name == e.Signal))
                _errors.Add(new ImportDiagnostic(e.SourceLine, "E_UNKNOWN_SIGNAL",
                    $"edge signal '{e.Signal}' is not declared", e.RawLine));
        foreach (var s in _samples)
        {
            foreach (var r in s.Voltages.Where(v => !_rails.Any(rd => rd.Name == v.Rail)))
                Warn(s.SourceLine, "W_ORPHAN_READING",
                    $"reading for undeclared rail '{r.Rail}' is kept but not analysed");
            foreach (var l in s.Logic.Where(l => !_signals.Any(sg => sg.Name == l.Signal)))
                Warn(s.SourceLine, "W_ORPHAN_LOGIC",
                    $"level for undeclared signal '{l.Signal}' is kept but not analysed");
        }
        var ordered = _samples.OrderBy(s => s.At.TotalMs!.Value).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1]; var cur = ordered[i];
            if (cur.Index == prev.Index + 1 && cur.At.TotalMs - prev.At.TotalMs > _clock!.TotalMs * 1.5)
                Warn(cur.SourceLine, "W_CLOCK_GAP",
                    $"sampling pause {Ms(cur.At.TotalMs - prev.At.TotalMs)} > 1.5x clock between sample {prev.Index} and {cur.Index}");
            if (cur.At.TotalMs < prev.At.TotalMs)
                _errors.Add(new ImportDiagnostic(cur.SourceLine, "E_TIME_TRAVEL",
                    $"sample {cur.Index} time precedes sample {prev.Index}", cur.RawLine));
        }
    }

    private static string Ms(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "ms";

    private void Warn(int line, string code, string msg)
    {
        if (!_warnings.Any(w => w.Line == line && w.Code == code))
            _warnings.Add(new ImportDiagnostic(line, code, msg, string.Empty));
    }

    private static void RequireIdent(string s, string what)
    {
        if (s.Length == 0 || !s.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ParseDslException("E_IDENT", $"{what} must be [A-Za-z0-9_]+, got '{s}'");
    }

    private static string RequireText(string s, int lineNo)
    {
        var t = s.Trim().Trim('"');
        if (t.Length == 0) throw new ParseDslException("E_TEXT", $"line {lineNo}: expected a quoted title");
        return t;
    }

    private static Pair SplitFirst(string s)
    {
        s = s.Trim();
        var i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return i >= s.Length ? new Pair(s, string.Empty) : new Pair(s[..i], s[(i + 1)..]);
    }

    private static IReadOnlyList<(string key, string val)> Fields(string s)
    {
        var list = new List<(string, string)>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            var key = s[start..i];
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            var vs = i;
            if (vs < s.Length && s[vs] == '"')
            {
                var end = s.IndexOf('"', vs + 1);
                if (end < 0) throw new ParseDslException("E_QUOTE", "unterminated quoted value");
                list.Add((key, s[(vs + 1)..end]));
                i = end + 1;
            }
            else
            {
                var ve = i;
                while (ve < s.Length && !char.IsWhiteSpace(s[ve])) ve++;
                list.Add((key, s[vs..ve]));
                i = ve;
            }
        }
        return list;
    }
}

public sealed class ParseDslException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
