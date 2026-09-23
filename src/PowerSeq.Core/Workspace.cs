namespace PowerSeq.Core;

public sealed record ResultView(DerivedResult Result, bool Stale, IReadOnlyList<string> StaleReasons);
public sealed record ImportOutcome(bool Ok, string? Id, IReadOnlyList<string> Errors);

/// <summary>
/// Orchestrates immutable inputs, derived results with provenance, staleness tracking,
/// human decisions and the append-only event log. Local input changes only expire the
/// results that depend on them; expired results stay visible but never pose as current.
/// </summary>
public sealed class Workspace : IDisposable
{
    public const string KindWaveforms = "waveforms";
    public const string KindThresholds = "thresholds";
    public const string KindGraph = "graph";

    private readonly Store _store;
    private readonly IClock _clock;

    public Workspace(Store store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public Store Store => _store;
    public IClock Clock => _clock;

    // ---------- import ----------

    public ImportOutcome ImportWaveforms(WaveformSet ws)
    {
        var errors = ValidateWaveforms(ws);
        if (errors.Count > 0) return new ImportOutcome(false, null, errors);
        var inserted = _store.InsertInput(KindWaveforms, ws.Id, ws.ParentId, Canon.Json(ws), ws.CreatedAt);
        if (inserted)
        {
            _store.AppendEvent(_clock.Now(), "input-submitted", new
            {
                kind = KindWaveforms, id = ws.Id, parentId = ws.ParentId, label = ws.Label,
                rails = ws.Rails.Select(r => r.Rail).ToArray(),
                preserved = new[] { "raw-waveform", "thresholds", "hysteresis", "min-stable-time", "dependency-graph", "sample-clock", "measurement-error" },
            });
            ComputeAll();
        }
        return new ImportOutcome(true, ws.Id, Array.Empty<string>());
    }

    public ImportOutcome ImportThresholds(ThresholdSet ts, string? forkNote = null)
    {
        var errors = ValidateThresholds(ts);
        if (errors.Count > 0) return new ImportOutcome(false, null, errors);
        var inserted = _store.InsertInput(KindThresholds, ts.Id, ts.ParentId, Canon.Json(ts), ts.CreatedAt);
        if (inserted)
        {
            _store.AppendEvent(_clock.Now(), ts.ParentId is null ? "input-submitted" : "threshold-version-forked", new
            {
                kind = KindThresholds, id = ts.Id, parentId = ts.ParentId, label = ts.Label, forkNote,
                rails = ts.Specs.Select(s => s.Rail).ToArray(),
            });
            ComputeAll();
        }
        return new ImportOutcome(true, ts.Id, Array.Empty<string>());
    }

    public ImportOutcome ImportGraph(DependencyGraph g)
    {
        var inserted = _store.InsertInput(KindGraph, g.Id, g.ParentId, Canon.Json(g), g.CreatedAt);
        if (inserted)
        {
            _store.AppendEvent(_clock.Now(), "input-submitted", new
            {
                kind = KindGraph, id = g.Id, parentId = g.ParentId, label = g.Label,
                edges = g.Edges.Select(e => $"{e.Before}->{e.After}").ToArray(),
            });
            ComputeAll();
        }
        return new ImportOutcome(true, g.Id, Array.Empty<string>());
    }

    private static List<string> ValidateWaveforms(WaveformSet ws)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ws.Label)) errors.Add("label is required");
        foreach (var r in ws.Rails)
        {
            if (string.IsNullOrWhiteSpace(r.Rail)) errors.Add("rail name is required");
            if (r.Role is not ("rail" or "enable" or "good")) errors.Add($"rail '{r.Rail}': role must be rail|enable|good, got '{r.Role}'");
            if (r.SamplePeriod <= 0) errors.Add($"rail '{r.Rail}': samplePeriod must be > 0");
            if (r.ValueError < 0) errors.Add($"rail '{r.Rail}': valueError must be >= 0");
            for (int i = 1; i < r.Samples.Count; i++)
                if (r.Samples[i].T < r.Samples[i - 1].T)
                    errors.Add($"rail '{r.Rail}': samples must be non-decreasing in time (index {i})");
        }
        return errors;
    }

    private static List<string> ValidateThresholds(ThresholdSet ts)
    {
        var errors = new List<string>();
        foreach (var s in ts.Specs)
        {
            if (s.FallThresholdMv >= s.RiseThresholdMv)
                errors.Add($"rail '{s.Rail}': fall threshold must be below rise threshold (hysteresis band)");
            if (s.MinStableMs <= 0)
                errors.Add($"rail '{s.Rail}': minStableMs must be > 0");
        }
        return errors;
    }

    // ---------- derivation ----------

    /// <summary>Computes every missing derived result for the active input versions. Deterministic: same inputs => same result ids.</summary>
    public IReadOnlyList<DerivedResult> ComputeAll()
    {
        var w = ActiveWaveforms();
        var t = ActiveThresholds();
        var g = ActiveGraph();
        var produced = new List<DerivedResult>();
        if (w is null || t is null) return produced;

        var windowsByRail = new Dictionary<string, StateWindowPayload>();
        foreach (var rail in w.Rails)
        {
            var spec = t.For(rail.Rail);
            if (spec is null) continue;
            var payload = Engine.ClassifyRail(rail, spec);
            windowsByRail[rail.Rail] = payload;
            var scope = new[] { new RailScope(rail.Rail, Engine.WaveHash(rail), Engine.ThresholdHash(spec)) };
            var fingerprint = Ids.Compute("fp", Canon.Json(new { alg = Engine.AlgClassify, rail.Rail, spec }));
            var result = DerivedResult.Create("state-windows", Engine.AlgClassify, fingerprint,
                new[] { w.Id, t.Id }, scope, payload, _clock.Now());
            if (_store.InsertResult(result))
            {
                produced.Add(result);
                EmitJudgments(result, payload);
            }
        }

        if (g is not null && windowsByRail.Count > 0)
        {
            var samplePeriods = w.Rails.ToDictionary(r => r.Rail, r => r.SamplePeriod);
            var errors = w.Rails.ToDictionary(r => r.Rail, r => (Engine.UnitFactorToMv(r.ValueUnit) ?? 1.0) * r.ValueError);
            var report = Engine.CheckSequence(windowsByRail, g, samplePeriods, errors);
            var scope = g.Edges.SelectMany(e => new[] { e.Before, e.After }).Distinct()
                .Select(rail =>
                {
                    var rw = w.Rails.FirstOrDefault(x => x.Rail == rail);
                    var spec = t.For(rail);
                    return new RailScope(rail, rw is null ? "" : Engine.WaveHash(rw), spec is null ? "" : Engine.ThresholdHash(spec));
                })
                .Concat(new[] { new RailScope("@graph", g.Id, "") }).ToList();
            var fingerprint = Ids.Compute("fp", Canon.Json(new { alg = Engine.AlgSequence, g.Id }));
            var result = DerivedResult.Create("sequence-report", Engine.AlgSequence, fingerprint,
                new[] { w.Id, t.Id, g.Id }, scope, report, _clock.Now());
            if (_store.InsertResult(result))
            {
                produced.Add(result);
                _store.AppendEvent(_clock.Now(), "judgment", new
                {
                    resultId = result.Id, kind = "sequence-report",
                    firstViolation = report.FirstViolation,
                    violations = report.Checks.Count(c => c.Status == "violation"),
                    ambiguous = report.Checks.Where(c => c.Status == "ambiguous").Select(c => c.EdgeId).ToArray(),
                    unknown = report.Checks.Where(c => c.Status == "unknown").Select(c => c.EdgeId).ToArray(),
                    basis = "rising/falling use different thresholds; shutdown checks reverse order; simultaneous arrivals keep all legal candidates",
                });
            }

            var cycle = Engine.CheckCycles(g);
            var cycleScope = new[] { new RailScope("@graph", g.Id, "") };
            var cycleFp = Ids.Compute("fp", Canon.Json(new { alg = Engine.AlgCycle, g.Id }));
            var cycleResult = DerivedResult.Create("cycle-report", Engine.AlgCycle, cycleFp,
                new[] { g.Id }, cycleScope, cycle, _clock.Now());
            if (_store.InsertResult(cycleResult))
            {
                produced.Add(cycleResult);
                _store.AppendEvent(_clock.Now(), "judgment", new
                {
                    resultId = cycleResult.Id, kind = "cycle-report",
                    hasCycle = cycle.HasCycle,
                    cycles = cycle.Cycles.Select(c => c.EdgeIds).ToArray(),
                    minimalContradictionEdgeSets = cycle.MinimalContradictionEdgeSets,
                    basis = cycle.Basis,
                });
            }
        }
        return produced;
    }

    private void EmitJudgments(DerivedResult result, StateWindowPayload payload)
    {
        foreach (var ex in payload.Excursions)
            _store.AppendEvent(_clock.Now(), "judgment", new
            {
                resultId = result.Id, kind = "excursion", rail = ex.Rail, verdict = ex.Verdict,
                startMs = ex.StartMs, endMs = ex.EndMs, durationMs = ex.DurationMs, basis = ex.Basis,
            });
        foreach (var win in payload.Windows.Where(x => x.State == "unknown"))
            _store.AppendEvent(_clock.Now(), "judgment", new
            {
                resultId = result.Id, kind = "unknown-window", rail = win.Rail,
                fromMs = win.FromMs, toMs = win.ToMs, basis = win.Basis,
            });
    }

    // ---------- queries ----------

    public WaveformSet? ActiveWaveforms() => _store.ActiveId(KindWaveforms) is { } id ? Canon.Parse<WaveformSet>(_store.GetInputJson(KindWaveforms, id)!) : null;
    public ThresholdSet? ActiveThresholds() => _store.ActiveId(KindThresholds) is { } id ? Canon.Parse<ThresholdSet>(_store.GetInputJson(KindThresholds, id)!) : null;
    public DependencyGraph? ActiveGraph() => _store.ActiveId(KindGraph) is { } id ? Canon.Parse<DependencyGraph>(_store.GetInputJson(KindGraph, id)!) : null;

    /// <summary>Current per-rail scope of the active input versions.</summary>
    public Dictionary<string, RailScope> CurrentScope()
    {
        var map = new Dictionary<string, RailScope>();
        var w = ActiveWaveforms();
        var t = ActiveThresholds();
        if (w is not null && t is not null)
            foreach (var rail in w.Rails)
            {
                var spec = t.For(rail.Rail);
                if (spec is not null)
                    map[rail.Rail] = new RailScope(rail.Rail, Engine.WaveHash(rail), Engine.ThresholdHash(spec));
            }
        var g = ActiveGraph();
        if (g is not null)
        {
            map["@graph"] = new RailScope("@graph", g.Id, "");
            if (w is not null && t is not null)
                foreach (var rail in g.Edges.SelectMany(e => new[] { e.Before, e.After }).Distinct())
                    if (!map.ContainsKey(rail))
                    {
                        var rw = w.Rails.FirstOrDefault(x => x.Rail == rail);
                        var spec = t.For(rail);
                        map[rail] = new RailScope(rail, rw is null ? "" : Engine.WaveHash(rw), spec is null ? "" : Engine.ThresholdHash(spec));
                    }
        }
        return map;
    }

    /// <summary>All results with staleness flags. Stale results stay visible but are never presented as current.</summary>
    public IReadOnlyList<ResultView> Results()
    {
        var current = CurrentScope();
        return _store.Results().Select(r =>
        {
            var reasons = new List<string>();
            foreach (var s in r.Scope)
            {
                if (!current.TryGetValue(s.Rail, out var cur))
                {
                    reasons.Add($"rail '{s.Rail}' no longer present in active inputs");
                    continue;
                }
                if (cur.WaveHash != s.WaveHash) reasons.Add($"rail '{s.Rail}': waveform source superseded");
                if (cur.ThresholdHash != s.ThresholdHash) reasons.Add($"rail '{s.Rail}': threshold version superseded");
            }
            return new ResultView(r, reasons.Count > 0, reasons);
        }).ToList();
    }

    public IReadOnlyList<EventRecord> Events() => _store.Events();

    /// <summary>
    /// Historical snapshot: the dependency versions and visible conclusions exactly as of
    /// the given event sequence number — never overwritten by newer computations.
    /// </summary>
    public (Dictionary<string, string> Active, IReadOnlyList<ResultView> Results) SnapshotAsOf(long eventSeq)
    {
        var inputs = _store.InputsUpTo(eventSeq);
        var active = inputs.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Seq).Last().Id);
        var scope = new Dictionary<string, RailScope>();
        var w = active.TryGetValue(KindWaveforms, out var wid) ? Canon.Parse<WaveformSet>(_store.GetInputJson(KindWaveforms, wid)!) : null;
        var t = active.TryGetValue(KindThresholds, out var tid) ? Canon.Parse<ThresholdSet>(_store.GetInputJson(KindThresholds, tid)!) : null;
        var g = active.TryGetValue(KindGraph, out var gid) ? Canon.Parse<DependencyGraph>(_store.GetInputJson(KindGraph, gid)!) : null;
        if (w is not null && t is not null)
            foreach (var rail in w.Rails)
            {
                var spec = t.For(rail.Rail);
                if (spec is not null)
                    scope[rail.Rail] = new RailScope(rail.Rail, Engine.WaveHash(rail), Engine.ThresholdHash(spec));
            }
        if (g is not null)
        {
            scope["@graph"] = new RailScope("@graph", g.Id, "");
            if (w is not null && t is not null)
                foreach (var rail in g.Edges.SelectMany(e => new[] { e.Before, e.After }).Distinct())
                    if (!scope.ContainsKey(rail))
                    {
                        var rw = w.Rails.FirstOrDefault(x => x.Rail == rail);
                        var spec = t.For(rail);
                        scope[rail] = new RailScope(rail, rw is null ? "" : Engine.WaveHash(rw), spec is null ? "" : Engine.ThresholdHash(spec));
                    }
        }
        var views = _store.ResultsUpTo(eventSeq).Select(x =>
        {
            var reasons = new List<string>();
            foreach (var s in x.Result.Scope)
            {
                if (!scope.TryGetValue(s.Rail, out var cur)) { reasons.Add($"rail '{s.Rail}' not present as of event #{eventSeq}"); continue; }
                if (cur.WaveHash != s.WaveHash) reasons.Add($"rail '{s.Rail}': waveform source superseded as of event #{eventSeq}");
                if (cur.ThresholdHash != s.ThresholdHash) reasons.Add($"rail '{s.Rail}': threshold version superseded as of event #{eventSeq}");
            }
            return new ResultView(x.Result, reasons.Count > 0, reasons);
        }).ToList();
        return (active, views);
    }
    public IReadOnlyList<Decision> Decisions() => _store.Decisions();

    // ---------- decisions & conflicts ----------

    public Decision Decide(string targetResultId, string verdict, string rationale, string author)
    {
        var d = Decision.Create(targetResultId, verdict, rationale, author, _clock.Now());
        if (!_store.InsertDecision(d)) return d;
        _store.AppendEvent(_clock.Now(), "decision-made", new { d.Id, targetResultId, verdict, rationale, author });

        var open = _store.Decisions()
            .Where(x => x.TargetResultId == targetResultId && x.UndoneBy is null && x.Id != d.Id)
            .ToList();
        var conflicting = open.Where(x => x.Verdict != verdict).ToList();
        if (conflicting.Count > 0)
            _store.AppendEvent(_clock.Now(), "conflict-recorded", new
            {
                targetResultId,
                decisions = conflicting.Select(x => x.Id).Concat(new[] { d.Id }).ToArray(),
                verdicts = conflicting.Select(x => x.Verdict).Concat(new[] { verdict }).ToArray(),
                basis = "mutually exclusive human verdicts on the same result; both retained until resolved",
            });
        return d;
    }

    public void UndoDecision(string decisionId, string author)
    {
        var ev = _store.AppendEvent(_clock.Now(), "decision-undone", new { decisionId, author });
        _store.MarkUndone(decisionId, ev.Id);
    }

    // ---------- archive ----------

    public string Export()
    {
        var json = _store.Export();
        _store.AppendEvent(_clock.Now(), "archive-exported", new { bytes = json.Length });
        return _store.Export();
    }

    public void ImportArchive(string json)
    {
        _store.ImportArchive(json);
        _store.AppendEvent(_clock.Now(), "archive-imported", new { bytes = json.Length });
    }

    public void Dispose() => _store.Dispose();
}
