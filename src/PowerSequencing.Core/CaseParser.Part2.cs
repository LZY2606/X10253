namespace PowerSequencing.Core;

public sealed partial class CaseParser
{
    private void ParseSignal(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var name = p.Head;
        RequireIdent(name, "signal name");
        var fields = Fields(p.Rest);
        string? label = null; var activeHigh = true;
        foreach (var (key, val) in fields)
        {
            switch (key.ToLowerInvariant())
            {
                case "label": label = val; break;
                case "active":
                    var av = val.ToLowerInvariant();
                    if (av is "high" or "1") activeHigh = true;
                    else if (av is "low" or "0") activeHigh = false;
                    else throw new ParseDslException("E_ACTIVE", "signal active expects high|low");
                    break;
                default: throw new ParseDslException("E_SIGNAL_FIELD",
                    $"unknown signal field '{key}' (expected label|active)");
            }
        }
        if (_signals.Any(s => s.Name == name))
            throw new ParseDslException("E_DUP_SIGNAL", $"signal '{name}' is already defined");
        _signals.Add(new SignalDef(name, label, activeHigh, lineNo, raw.TrimEnd()));
    }

    private void ParseEdge(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var spec = p.Head;
        var arrow = spec.IndexOf("->", StringComparison.Ordinal);
        if (arrow <= 0 || arrow + 2 >= spec.Length)
            throw new ParseDslException("E_EDGE", "edge expects <signal>->up|down");
        var sig = spec[..arrow];
        var dir = spec[(arrow + 2)..].ToLowerInvariant();
        if (dir is not ("up" or "down"))
            throw new ParseDslException("E_EDGE", "edge direction must be up or down");
        RequireIdent(sig, "signal name");
        var f = Fields(p.Rest);
        RawTime? at = null;
        foreach (var (key, val) in f)
        {
            if (key.Equals("at", StringComparison.OrdinalIgnoreCase))
            {
                at = TimeParser.Parse(val);
                if (!at.IsValid) throw new ParseDslException("E_TIME", at.Error!);
            }
            else throw new ParseDslException("E_EDGE_FIELD", $"unknown edge field '{key}' (expected at)");
        }
        if (at is null) throw new ParseDslException("E_EDGE_TIME", "edge requires at <time>");
        _edges.Add(new EdgeEvent(sig, dir == "up", at, lineNo, raw.TrimEnd()));
    }

    private void ParseDep(string rest, int lineNo, string raw)
    {
        var p = SplitFirst(rest);
        var spec = p.Head;
        var arrow = spec.IndexOf("->", StringComparison.Ordinal);
        if (arrow <= 0 || arrow + 2 >= spec.Length)
            throw new ParseDslException("E_DEP", "dep expects <rail>-><rail>");
        var from = spec[..arrow];
        var to = spec[(arrow + 2)..];
        RequireIdent(from, "dependency source rail");
        RequireIdent(to, "dependency target rail");
        if (from == to)
            throw new ParseDslException("E_DEP_SELF", $"rail '{from}' cannot depend on itself in the input graph");
        RawTime? minGap = null, maxGap = null!;
        foreach (var (key, val) in Fields(p.Rest))
        {
            switch (key.ToLowerInvariant())
            {
                case "min_gap":
                    minGap = TimeParser.Parse(val);
                    if (!minGap.IsValid) throw new ParseDslException("E_TIME", minGap.Error!);
                    break;
                case "max_gap":
                    maxGap = TimeParser.Parse(val);
                    if (!maxGap.IsValid) throw new ParseDslException("E_TIME", maxGap.Error!);
                    break;
                default: throw new ParseDslException("E_DEP_FIELD",
                    $"unknown dep field '{key}' (expected min_gap|max_gap)");
            }
        }
        if (maxGap is null)
            throw new ParseDslException("E_DEP_REQUIRED", "dep requires max_gap <time>");
        if (minGap is not null && minGap.TotalMs > maxGap.TotalMs)
            throw new ParseDslException("E_DEP_RANGE", "min_gap must be <= max_gap");
        if (_deps.Any(d => d.Slot == $"{from}->{to}"))
            throw new ParseDslException("E_DUP_DEP", $"dependency {from}->{to} is already defined");
        _deps.Add(new DependencyEdge(from, to, minGap, maxGap, lineNo, raw.TrimEnd()));
    }
