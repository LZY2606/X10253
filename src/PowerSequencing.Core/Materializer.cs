namespace PowerSequencing.Core;

/// <summary>
/// Replays immutable commits into a <see cref="MaterializedState"/>.  Replacements
/// swap the projected item while the previous raw material stays stored in its
/// own commit; provenance always points at the newest contributing commit.
/// </summary>
public static class Materializer
{
    public static MaterializedState Apply(string caseId, string commitId, MaterializedState? parent,
        CaseDoc doc)
    {
        var s = Fresh(caseId, commitId, doc);
        if (parent is not null)
            throw new InvalidOperationException("baseline case builds a fresh state");
        return s;
    }

    public static MaterializedState Apply(string caseId, string commitId, MaterializedState parent,
        PatchDoc patch)
    {
        var s = Clone(caseId, commitId, parent);
        foreach (var op in patch.Ops)
        {
            switch (op)
            {
                case AddDepOp a:
                    var edge = new DependencyEdge(a.From, a.To, a.MinGap, a.MaxGap, a.Line, a.Raw);
                    s.Deps[edge.Slot] = new DepState(edge, new Origin(commitId, a.Line, a.Raw, op.Kind));
                    break;
                case RemoveDepOp r:
                    s.Deps.Remove($"{r.From}->{r.To}");
                    break;
                case UpdateRailOp u:
                    if (!s.Rails.TryGetValue(u.Rail, out var cur))
                        throw new InvalidOperationException(
                            $"update_rail targets unknown rail '{u.Rail}' (line {u.Line})");
                    var def = new RailDef(u.Rail, cur.Def.Label,
                        u.Rise ?? cur.Def.RiseThreshold,
                        u.Fall ?? cur.Def.FallThreshold,
                        u.MinStable ?? cur.Def.MinStable,
                        u.GlitchMax ?? cur.Def.GlitchTolerance,
                        u.Line, u.Raw);
                    s.Rails[u.Rail] = new RailState(def,
                        new Origin(commitId, u.Line, u.Raw, op.Kind),
                        StableId.Of("params", string.Join("\u0001", u.Rail,
                            def.RiseThreshold.Raw, def.FallThreshold.Raw,
                            def.MinStable.Raw, def.GlitchTolerance.Raw)),
                        commitId);
                    break;
                case ReplaceSampleOp rs:
                    if (!s.Samples.TryGetValue(rs.Index, out var old))
                        throw new InvalidOperationException(
                            $"replace_sample targets missing sample index {rs.Index} (line {rs.Line})");
                    var point = old.Point;
                    var kept = point.Voltages.Where(v => v.Rail != rs.Rail).ToList();
                    kept.Add(new RailReading(rs.Rail, rs.Value, rs.Unc, rs.Line, rs.Value.Raw));
                    var replaced = new SamplePoint(point.Index, point.At,
                        kept.OrderBy(v => v.Rail).ToList(), point.Logic, rs.Line, rs.Raw);
                    s.Samples[rs.Index] = new SampleState(replaced,
                        new Origin(commitId, rs.Line, rs.Raw, op.Kind),
                        StableId.Of("sample", $"{rs.Index}\u0001{rs.Raw}"));
                    break;
                case AddSampleOp add:
                    s.Samples[add.Sample.Index] = new SampleState(add.Sample,
                        new Origin(commitId, add.Line, add.Raw, op.Kind),
                        StableId.Of("sample", $"{add.Sample.Index}\u0001{add.Raw}"));
                    break;
                case AddGapOp g:
                    s.Gaps.Add(new GapState(g.Gap, new Origin(commitId, g.Line, g.Raw, "gap")));
                    break;
            }
        }
        return s;
    }

    private static MaterializedState Fresh(string caseId, string commitId, CaseDoc doc)
    {
        var s = new MaterializedState { CaseId = caseId, CommitId = commitId, ClockPeriod = doc.ClockPeriod! };
        foreach (var r in doc.Rails)
        {
            var pf = StableId.Of("params", string.Join("\u0001", r.Name,
                r.RiseThreshold.Raw, r.FallThreshold.Raw, r.MinStable.Raw, r.GlitchTolerance.Raw));
            s.Rails[r.Name] = new RailState(r, new Origin(commitId, r.SourceLine, r.RawLine, "rail"), pf, null);
        }
        foreach (var sig in doc.Signals) s.Signals[sig.Name] = sig;
        s.Edges.AddRange(doc.Edges);
        foreach (var d in doc.Deps)
            s.Deps[d.Slot] = new DepState(d, new Origin(commitId, d.SourceLine, d.RawLine, "dep"));
        foreach (var sp in doc.Samples)
            s.Samples[sp.Index] = new SampleState(sp,
                new Origin(commitId, sp.SourceLine, sp.RawLine, "sample"),
                StableId.Of("sample", $"{sp.Index}\u0001{sp.RawLine}"));
        s.Phases.AddRange(doc.Phases);
        foreach (var g in doc.Gaps)
            s.Gaps.Add(new GapState(g, new Origin(commitId, g.SourceLine, g.RawLine, "gap")));
        return s;
    }

    private static MaterializedState Clone(string caseId, string commitId, MaterializedState p) =>
        new()
        {
            CaseId = caseId,
            CommitId = commitId,
            ClockPeriod = p.ClockPeriod,
            Rails = new Dictionary<string, RailState>(p.Rails),
            Signals = new Dictionary<string, SignalDef>(p.Signals),
            Edges = [.. p.Edges],
            Deps = new Dictionary<string, DepState>(p.Deps),
            Samples = new Dictionary<int, SampleState>(p.Samples),
            Phases = [.. p.Phases],
            Gaps = [.. p.Gaps]
        };
}
