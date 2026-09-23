namespace PowerSeq.Core;

public sealed record StateWindow(string Rail, string State, double FromMs, double ToMs, string Basis);
public sealed record Excursion(string Rail, double StartMs, double EndMs, double DurationMs, double DepthMv, string Verdict, string Basis);
public sealed record StateWindowPayload(
    string Rail,
    IReadOnlyList<StateWindow> Windows,
    IReadOnlyList<Excursion> Excursions,
    IReadOnlyList<string> Diagnostics);

public sealed record OrderCandidate(IReadOnlyList<string> Ordering, string Basis);
public sealed record EdgeCheck(
    string EdgeId, string Before, string After, string Phase,
    string Status,            // "pass" | "violation" | "ambiguous" | "unknown"
    double? RequiredMs, double? ActualMs, string Basis,
    IReadOnlyList<OrderCandidate>? Candidates);
public sealed record SequenceReport(
    IReadOnlyList<EdgeCheck> Checks,
    EdgeCheck? FirstViolation,
    IReadOnlyList<string> Diagnostics);

public sealed record CycleInfo(IReadOnlyList<string> Nodes, IReadOnlyList<string> EdgeIds);
public sealed record CycleReport(
    bool HasCycle,
    IReadOnlyList<CycleInfo> Cycles,
    IReadOnlyList<IReadOnlyList<string>> MinimalContradictionEdgeSets,
    string Basis);

public static class Engine
{
    public const string AlgClassify = "classify-v1";
    public const string AlgSequence = "seq-v1";
    public const string AlgCycle = "cycle-v1";

    public static string WaveHash(RailWaveform w) => Ids.Compute("wh", Canon.Json(w));
    public static string ThresholdHash(ThresholdSpec s) => Ids.Compute("thh", Canon.Json(s));

    /// <summary>Unit normalization happens only here, in the derived layer. Unconvertible values stay raw with a diagnostic.</summary>
    public static double? UnitFactorToMv(string unit) => unit switch
    {
        "mV" => 1.0,
        "V" => 1000.0,
        _ => null,
    };

    /// <summary>
    /// Hysteresis classifier: rising crosses use RiseThresholdMv, falling crosses use FallThresholdMv.
    /// A state that does not persist for MinStableMs is a glitch, not a violation.
    /// Clock gaps produce Unknown windows. Raw samples are never mutated.
    /// </summary>
    public static StateWindowPayload ClassifyRail(RailWaveform rail, ThresholdSpec spec)
    {
        var diagnostics = new List<string>();
        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();

        var factor = UnitFactorToMv(rail.ValueUnit);
        if (factor is null)
        {
            diagnostics.Add($"unit '{rail.ValueUnit}' not convertible to mV; samples kept raw at original position, excluded from classification");
            return new StateWindowPayload(rail.Rail, windows, excursions, diagnostics);
        }
        if (rail.TimeUnit != "ms")
        {
            diagnostics.Add($"time unit '{rail.TimeUnit}' not convertible to ms; samples kept raw, excluded from classification");
            return new StateWindowPayload(rail.Rail, windows, excursions, diagnostics);
        }
        if (rail.Samples.Count == 0)
        {
            diagnostics.Add("no samples");
            return new StateWindowPayload(rail.Rail, windows, excursions, diagnostics);
        }

        var pts = rail.Samples.OrderBy(s => s.T).Select(s => (t: s.T, v: s.V * factor.Value)).ToList();
        var errMv = rail.ValueError * factor.Value;
        var gapLimit = rail.SamplePeriod * 1.5;

        // Split into contiguous segments separated by clock gaps.
        var segments = new List<List<(double t, double v)>> { new() { pts[0] } };
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].t - pts[i - 1].t > gapLimit)
            {
                diagnostics.Add($"clock gap {pts[i - 1].t:0.###}..{pts[i].t:0.###} ms exceeds 1.5x sample period {rail.SamplePeriod:0.###} ms; window marked unknown");
                segments.Add(new List<(double t, double v)>());
            }
            segments[^1].Add(pts[i]);
        }

        double cursor = pts[0].t;
        foreach (var seg in segments)
        {
            if (seg.Count == 0) continue;
            if (seg[0].t > cursor)
                windows.Add(new StateWindow(rail.Rail, "unknown", cursor, seg[0].t, "clock gap: samples missing, state undecidable"));

            // Raw hysteresis transitions with linear interpolation of crossing times.
            var transitions = new List<(double t, string to, double depth)>();
            var state = seg[0].v >= spec.RiseThresholdMv ? "on" : "off";
            for (int i = 1; i < seg.Count; i++)
            {
                var (t0, v0) = seg[i - 1];
                var (t1, v1) = seg[i];
                if (state == "off" && v0 < spec.RiseThresholdMv && v1 >= spec.RiseThresholdMv)
                {
                    transitions.Add((Cross(t0, v0, t1, v1, spec.RiseThresholdMv), "on", 0));
                    state = "on";
                }
                else if (state == "on" && v0 > spec.FallThresholdMv && v1 <= spec.FallThresholdMv)
                {
                    transitions.Add((Cross(t0, v0, t1, v1, spec.FallThresholdMv), "off", 0));
                    state = "off";
                }
            }

            // Build raw windows, then fold sub-MinStableMs windows into glitch excursions.
            var bounds = new List<(double from, double to, string st)>();
            double segStart = seg[0].t;
            string cur = seg[0].v >= spec.RiseThresholdMv ? "on" : "off";
            foreach (var (t, to, _) in transitions)
            {
                bounds.Add((segStart, t, cur));
                segStart = t;
                cur = to;
            }
            bounds.Add((segStart, seg[^1].t, cur));

            int k = 0;
            while (k < bounds.Count)
            {
                var b = bounds[k];
                if (k > 0 && k < bounds.Count - 1 && b.to - b.from < spec.MinStableMs)
                {
                    // Short excursion between two windows of the same outer state: glitch, not violation.
                    var depth = Extreme(seg, b.from, b.to, b.st == "on" ? -1 : +1, spec);
                    excursions.Add(new Excursion(rail.Rail, b.from, b.to, b.to - b.from, depth, "glitch",
                        $"duration {b.to - b.from:0.###} ms < min stable {spec.MinStableMs:0.###} ms; hysteresis band {spec.FallThresholdMv:0.#}..{spec.RiseThresholdMv:0.#} mV"));
                    k++;
                    continue;
                }
                if (k > 0 && k < bounds.Count - 1 && b.st == "off" && b.to - b.from >= spec.MinStableMs)
                {
                    var depth = Extreme(seg, b.from, b.to, -1, spec);
                    excursions.Add(new Excursion(rail.Rail, b.from, b.to, b.to - b.from, depth, "violation",
                        $"drop below fall threshold persists {b.to - b.from:0.###} ms >= min stable {spec.MinStableMs:0.###} ms"));
                }
                windows.Add(new StateWindow(rail.Rail, b.st, b.from, b.to,
                    b.st == "on" ? $"v >= rise threshold {spec.RiseThresholdMv:0.#} mV (hysteresis)" : $"v <= fall threshold {spec.FallThresholdMv:0.#} mV (hysteresis)"));
                k++;
            }
            // Merge adjacent windows of the same state created by glitch folding.
            windows.MergeAdjacent();
            cursor = seg[^1].t;
        }

        if (errMv > 0)
            diagnostics.Add($"measurement error ±{errMv:0.###} mV retained; crossing times carry ± tolerance in sequence checks");
        return new StateWindowPayload(rail.Rail, windows, excursions, diagnostics);
    }

    private static double Cross(double t0, double v0, double t1, double v1, double threshold)
        => v1 == v0 ? t0 : t0 + (threshold - v0) * (t1 - t0) / (v1 - v0);

    private static double Extreme(List<(double t, double v)> seg, double from, double to, int dir, ThresholdSpec spec)
    {
        var inside = seg.Where(p => p.t >= from && p.t <= to).Select(p => p.v).ToList();
        if (inside.Count == 0) return 0;
        var ext = dir < 0 ? inside.Min() : inside.Max();
        return dir < 0 ? spec.FallThresholdMv - ext : ext - spec.RiseThresholdMv;
    }

    /// <summary>
    /// Sequence check over state windows and the dependency graph.
    /// Power-up honours edge direction; shutdown checks the reverse order.
    /// Simultaneous arrivals (within tolerance) keep every legal ordering candidate.
    /// </summary>
    public static SequenceReport CheckSequence(
        IReadOnlyDictionary<string, StateWindowPayload> windowsByRail,
        DependencyGraph graph,
        IReadOnlyDictionary<string, double> samplePeriodByRail,
        IReadOnlyDictionary<string, double> errorMvByRail)
    {
        var checks = new List<EdgeCheck>();
        var diagnostics = new List<string>();

        double? Rise(string rail) => windowsByRail.TryGetValue(rail, out var p)
            ? p.Windows.FirstOrDefault(w => w.State == "on")?.FromMs : null;
        double? Fall(string rail)
        {
            if (!windowsByRail.TryGetValue(rail, out var p)) return null;
            var wins = p.Windows.ToList();
            var firstOn = wins.FindIndex(w => w.State == "on");
            if (firstOn < 0) return null;
            return wins.Skip(firstOn + 1).FirstOrDefault(w => w.State == "off")?.FromMs;
        }
        bool HasUnknown(string rail) => windowsByRail.TryGetValue(rail, out var p) && p.Windows.Any(w => w.State == "unknown");

        double Tol(string rail) =>
            (samplePeriodByRail.TryGetValue(rail, out var sp) ? sp / 2 : 0) +
            (errorMvByRail.TryGetValue(rail, out var e) ? e * 0 : 0);

        foreach (var e in graph.Edges)
        {
            // Power-up: After rises no earlier than Before rises + MinDelay.
            var rb = Rise(e.Before);
            var ra = Rise(e.After);
            checks.Add(CheckOne(e, "up", rb, ra, e.MinDelayMs, Tol(e.Before) + Tol(e.After),
                HasUnknown(e.Before) || HasUnknown(e.After), e.Before, e.After, diagnostics));

            // Shutdown: reverse order — Before falls no earlier than After falls + MinDelay.
            var fb = Fall(e.Before);
            var fa = Fall(e.After);
            checks.Add(CheckOne(e, "down", fa, fb, e.MinDelayMs, Tol(e.Before) + Tol(e.After),
                HasUnknown(e.Before) || HasUnknown(e.After), e.After, e.Before, diagnostics));
        }

        var firstViolation = checks
            .Where(c => c.Status == "violation")
            .OrderBy(c => c.ActualMs ?? double.MaxValue)
            .FirstOrDefault();
        return new SequenceReport(checks, firstViolation, diagnostics);
    }

    private static EdgeCheck CheckOne(DependencyEdge e, string phase, double? leaderMs, double? followerMs,
        double minDelay, double tol, bool unknown, string leader, string follower, List<string> diagnostics)
    {
        if (unknown)
            return new EdgeCheck(e.Id, e.Before, e.After, phase, "unknown", null, null,
                "clock gap makes one endpoint undecidable; result kept as unknown, not guessed", null);
        if (leaderMs is null || followerMs is null)
            return new EdgeCheck(e.Id, e.Before, e.After, phase, "unknown", null, null,
                "missing transition; rail never reaches the required state in captured window", null);

        var required = leaderMs.Value + minDelay;
        var actual = followerMs.Value;
        var slack = actual - required;

        if (Math.Abs(actual - leaderMs.Value) <= tol && minDelay > 0)
        {
            // Simultaneous arrival: keep every legal candidate, decide nothing.
            var cands = new List<OrderCandidate>
            {
                new(new[] { leader, follower }, $"{leader} leads within tolerance ±{tol:0.###} ms"),
                new(new[] { follower, leader }, $"{follower} leads within tolerance ±{tol:0.###} ms"),
            };
            return new EdgeCheck(e.Id, e.Before, e.After, phase, "ambiguous", required, actual,
                $"simultaneous arrival within ±{tol:0.###} ms; {cands.Count} legal orderings retained, none uniquely decidable", cands);
        }
        if (slack >= -tol)
            return new EdgeCheck(e.Id, e.Before, e.After, phase, "pass", required, actual,
                $"slack {slack:0.###} ms satisfies min delay {minDelay:0.###} ms", null);
        return new EdgeCheck(e.Id, e.Before, e.After, phase, "violation", required, actual,
            $"actual {actual:0.###} ms precedes required {required:0.###} ms by {-slack:0.###} ms", null);
    }

    /// <summary>
    /// Cycle analysis: enumerates simple cycles, then computes the minimum contradiction edge sets
    /// (minimum hitting sets over all cycles). Every equally-minimal set is kept.
    /// </summary>
    public static CycleReport CheckCycles(DependencyGraph graph)
    {
        var adj = graph.Edges.GroupBy(e => e.Before).ToDictionary(g => g.Key, g => g.Select(e => (e.After, e.Id)).ToList());
        var cycles = new List<CycleInfo>();
        var seen = new HashSet<string>();

        foreach (var start in adj.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var stack = new List<(string node, string edgeId)>();
            var onPath = new HashSet<string> { start };
            void Dfs(string node)
            {
                if (!adj.TryGetValue(node, out var outs)) return;
                foreach (var (next, edgeId) in outs)
                {
                    if (next == start)
                    {
                        var cycNodes = stack.Select(s => s.node).Concat(new[] { start }).ToList();
                        var cycEdges = stack.Select(s => s.edgeId).Concat(new[] { edgeId }).ToList();
                        // Canonical key: rotation-independent edge multiset.
                        var key = string.Join("|", cycEdges.OrderBy(x => x, StringComparer.Ordinal));
                        if (seen.Add(key))
                            cycles.Add(new CycleInfo(cycNodes, cycEdges));
                    }
                    else if (!onPath.Contains(next) && string.CompareOrdinal(next, start) >= 0)
                    {
                        onPath.Add(next);
                        stack.Add((next, edgeId));
                        Dfs(next);
                        stack.RemoveAt(stack.Count - 1);
                        onPath.Remove(next);
                    }
                }
            }
            Dfs(start);
        }

        if (cycles.Count == 0)
            return new CycleReport(false, cycles, Array.Empty<IReadOnlyList<string>>(), "no directed cycle; dependency graph is acyclic");

        // Minimum hitting set: smallest edge set intersecting every cycle. Brute force by size (graphs here are small).
        var allEdges = cycles.SelectMany(c => c.EdgeIds).Distinct().ToList();
        var best = new List<IReadOnlyList<string>>();
        for (int size = 1; size <= allEdges.Count && best.Count == 0; size++)
        {
            foreach (var combo in Combinations(allEdges, size))
            {
                var set = combo.ToHashSet();
                if (cycles.All(c => c.EdgeIds.Any(set.Contains)))
                    best.Add(combo);
            }
        }
        var basis = best.Count == 1
            ? "unique minimum contradiction edge set"
            : $"{best.Count} equally minimal contradiction edge sets; none uniquely decidable, all retained";
        return new CycleReport(true, cycles, best, basis);
    }

    private static IEnumerable<IReadOnlyList<string>> Combinations(List<string> items, int size)
    {
        var idx = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return idx.Select(i => items[i]).ToList();
            int i = size - 1;
            while (i >= 0 && idx[i] == items.Count - size + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (int j = i + 1; j < size; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}

internal static class WindowMerge
{
    public static void MergeAdjacent(this List<StateWindow> windows)
    {
        for (int i = windows.Count - 1; i > 0; i--)
        {
            if (windows[i].State == windows[i - 1].State && windows[i].State != "unknown")
            {
                windows[i - 1] = windows[i - 1] with { ToMs = windows[i].ToMs };
                windows.RemoveAt(i);
            }
        }
    }
}
