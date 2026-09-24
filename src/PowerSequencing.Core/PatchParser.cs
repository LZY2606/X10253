namespace PowerSequencing.Core;

/// <summary>
/// Parses revision files.  Revisions never mutate stored raw input: they only
/// produce new <see cref="PatchOp"/> statements applied on top of a parent
/// materialisation.
/// </summary>
public static class PatchParser
{
    public static PatchDoc Parse(string text)
    {
        var ops = new List<PatchOp>();
        var errors = new List<ImportDiagnostic>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? message = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var lineNo = i + 1;
            try
            {
                var p = SplitFirst(line);
                switch (p.Head.ToLowerInvariant())
                {
                    case "message": message = p.Rest.Trim().Trim('"'); break;
                    case "add_dep": ops.Add(AddDep(p.Rest, lineNo, raw)); break;
                    case "remove_dep": ops.Add(RemoveDep(p.Rest, lineNo, raw)); break;
                    case "update_rail": ops.Add(UpdateRail(p.Rest, lineNo, raw)); break;
                    case "replace_sample": ops.Add(ReplaceSample(p.Rest, lineNo, raw)); break;
                    case "add_sample": ops.Add(AddSample(p.Rest, lineNo, raw)); break;
                    case "gap": ops.Add(new AddGapOp(ParseGap(p.Rest, lineNo, raw), lineNo, raw.TrimEnd())); break;
                    default:
                        throw new ParseDslException("E_UNKNOWN_PATCH",
                            $"unknown revision op '{p.Head}' (expected add_dep|remove_dep|update_rail|replace_sample|add_sample|gap)");
                }
            }
            catch (ParseDslException ex)
            {
                errors.Add(new ImportDiagnostic(lineNo, ex.Code, ex.Message, raw.TrimEnd()));
            }
        }
        return new PatchDoc(message, ops, errors, text.Replace("\r\n", "\n"));
    }

    private static AddDepOp AddDep(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var (from, to) = EdgePair(p.Head, "add_dep");
        RawTime? min = null; var max = RequireTimeField(p.Rest, "max_gap");
        foreach (var (k, v) in Fields(p.Rest))
            if (k.Equals("min_gap", StringComparison.OrdinalIgnoreCase))
            {
                min = TimeParser.Parse(v);
                if (!min.IsValid) throw new ParseDslException("E_TIME", min.Error!);
            }
        if (min is not null && min.TotalMs > max.TotalMs)
            throw new ParseDslException("E_DEP_RANGE", "min_gap must be <= max_gap");
        return new AddDepOp(from, to, min, max, lineNo, raw.TrimEnd());
    }

    private static RemoveDepOp RemoveDep(string rest, int lineNo, string raw)
    {
        var (from, to) = EdgePair(SplitFirst(rest).Head, "remove_dep");
        return new RemoveDepOp(from, to, lineNo, raw.TrimEnd());
    }

    private static UpdateRailOp UpdateRail(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var rail = p.Head;
        RequireIdentLocal(rail);
        RawQuantity? rise = null, fall = null; RawTime? minStable = null, glitch = null;
        foreach (var (k, v) in Fields(p.Rest))
        {
            switch (k.ToLowerInvariant())
            {
                case "rise_at":
                    rise = QuantityParser.Parse(v);
                    if (!rise.IsConvertible) throw new ParseDslException("E_UNIT", rise.Error!);
                    break;
                case "fall_below":
                    fall = QuantityParser.Parse(v);
                    if (!fall.IsConvertible) throw new ParseDslException("E_UNIT", fall.Error!);
                    break;
                case "min_stable":
                    minStable = TimeParser.Parse(v);
                    if (!minStable.IsValid) throw new ParseDslException("E_TIME", minStable.Error!);
                    break;
                case "glitch_max":
                    glitch = TimeParser.Parse(v);
                    if (!glitch.IsValid) throw new ParseDslException("E_TIME", glitch.Error!);
                    break;
                default: throw new ParseDslException("E_RAIL_FIELD",
                    $"unknown update_rail field '{k}' (expected rise_at|fall_below|min_stable|glitch_max)");
            }
        }
        return new UpdateRailOp(rail, rise, fall, minStable, glitch, lineNo, raw.TrimEnd());
    }

    private static ReplaceSampleOp ReplaceSample(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        if (!int.TryParse(p.Head, out var idx) || idx < 0)
            throw new ParseDslException("E_SAMPLE_INDEX", "replace_sample index must be a non-negative integer");
        string rail = null!; RawQuantity value = null!; RawQuantity? unc = null;
        foreach (var (k, v) in Fields(p.Rest))
        {
            if (k.Equals("rail", StringComparison.OrdinalIgnoreCase)) { rail = v; RequireIdentLocal(rail); }
            else if (k.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                var pr = CaseParser.ParseReadingPublic(v);
                value = pr.value; unc = pr.unc;
            }
            else throw new ParseDslException("E_SAMPLE_FIELD",
                $"unknown replace_sample field '{k}' (expected rail|value)");
        }
        if (rail is null || value is null)
            throw new ParseDslException("E_SAMPLE_REQUIRED", "replace_sample requires rail <name> value <q>");
        return new ReplaceSampleOp(idx, rail, value, unc, lineNo, raw.TrimEnd());
    }

    private static AddSampleOp AddSample(string rest, int lineNo, string raw)
    {
        // Delegates full sample syntax to the case parser by synthesising a line.
        var synth = "sample " + rest;
        var doc = CaseParser.ParseCase(synth + "\n");
        if (!doc.IsValid)
        {
            var e = doc.Errors.First(x => x.Code != "E_NO_CLOCK" && x.Code != "E_NO_RAILS");
            throw new ParseDslException(e.Code, e.Message);
        }
        return new AddSampleOp(doc.Samples[0], lineNo, raw.TrimEnd());
    }

    private static GapAnnotation ParseGap(string rest, int lineNo, string raw)
    {
        var doc = CaseParser.ParseCase("clock period 1ms\n" + "gap " + rest + "\n");
        if (!doc.IsValid)
        {
            var e = doc.Errors.First(x => x.Code != "E_NO_RAILS");
            throw new ParseDslException(e.Code, e.Message);
        }
        return doc.Gaps[0];
    }

    private static RawTime RequireTimeField(string rest, string name)
    {
        foreach (var (k, v) in Fields(rest))
            if (k.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var t = TimeParser.Parse(v);
                if (!t.IsValid) throw new ParseDslException("E_TIME", t.Error!);
                return t;
            }
        throw new ParseDslException("E_MISSING_FIELD", $"missing required field {name}");
    }

    private static (string from, string to) EdgePair(string spec, string op)
    {
        var arrow = spec.IndexOf("->", StringComparison.Ordinal);
        if (arrow <= 0 || arrow + 2 >= spec.Length)
            throw new ParseDslException("E_EDGE_SPEC", $"{op} expects <rail>-><rail>");
        var from = spec[..arrow]; var to = spec[(arrow + 2)..];
        RequireIdentLocal(from); RequireIdentLocal(to);
        return (from, to);
    }

    private static void RequireIdentLocal(string s)
    {
        if (s.Length == 0 || !s.All(char.IsLetterOrDigit))
            throw new ParseDslException("E_IDENT", $"'{s}' is not a valid rail identifier");
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf('#');
        return i < 0 ? line : line[..i];
    }

    private sealed record Pair(string Head, string Rest);
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
            var ks = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            var key = s[ks..i];
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            var ve = i;
            while (ve < s.Length && !char.IsWhiteSpace(s[ve])) ve++;
            list.Add((key, s[i..ve]));
            i = ve;
        }
        return list;
    }
}
