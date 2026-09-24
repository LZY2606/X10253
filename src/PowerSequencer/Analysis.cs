using System.Text.Json.Nodes;

namespace PowerSequencer;

/// <summary>
/// window-v1: rising and falling use different thresholds; hysteresis widens the
/// qualification band; an excursion shorter than min-stable is a glitch, at or
/// beyond min-stable it is a violation (while Good) or a real transition (while Off).
/// Clock gaps produce Unknown windows. All judgements carry their basis.
/// </summary>
public static class WindowAnalyzer
{
    public static RailAnalysis Analyze(
        RailSeries series,
        RailThreshold th,
        ClockSpec clock,
        string thresholdVersionId)
    {
        var windows = new List<StateWindow>();
        var events = new List<AnalysisEvent>();
        var pts = series.Samples;
        var state = RailState.Off;
        var winStart = pts.Count > 0 ? pts[0].T : 0.0;

        JsonObject Basis() => new()
        {
            ["algo"] = Algo.Window,
            ["thresholdVersion"] = thresholdVersionId,
            ["riseV"] = th.RiseV,
            ["fallV"] = th.FallV,
            ["hysteresisV"] = th.HysteresisV,
            ["minStableNs"] = th.MinStableNs,
            ["clockPeriodNs"] = clock.PeriodNs,
            ["clockJitterNs"] = clock.JitterNs,
            ["measurementErrorV"] = clock.ErrorV,
        };

        void CloseWindow(double tEnd, string reason)
        {
            if (tEnd > winStart || windows.Count == 0)
                windows.Add(new StateWindow(state, winStart, tEnd, reason));
        }

        // Qualification tracking for an in-progress excursion.
        var qualifying = false;
        var qualDir = "";
        var qualStart = 0.0;

        void AbortQualification(double t, double lastT)
        {
            if (!qualifying) return;
            var dur = Math.Max(0.0, lastT - qualStart);
            events.Add(new AnalysisEvent(
                "glitch", series.Rail, qualStart, dur, qualDir,
                Basis()));
            qualifying = false;
        }

        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];

            // Clock gap detection: missing samples => unknown region.
            if (i > 0)
            {
                var dt = p.T - pts[i - 1].T;
                var gapLimit = 2.5 * clock.PeriodNs + clock.JitterNs;
                if (dt > gapLimit)
                {
                    AbortQualification(pts[i - 1].T, pts[i - 1].T);
                    CloseWindow(pts[i - 1].T, "before-clock-gap");
                    winStart = pts[i - 1].T;
                    var prev = state;
                    state = RailState.Unknown;
                    windows.Add(new StateWindow(RailState.Unknown, pts[i - 1].T, p.T, "clock-gap"));
                    events.Add(new AnalysisEvent("unknown-region", series.Rail, pts[i - 1].T,
                        p.T - pts[i - 1].T, "none",
                        new JsonObject(Basis()) { ["reason"] = "clock-gap" }));
                    state = prev;
                    winStart = p.T;
                }
            }

            if (state == RailState.Off)
            {
                if (!qualifying)
                {
                    if (p.V >= th.RiseV)
                    {
                        qualifying = true;
                        qualDir = "rise";
                        qualStart = p.T;
                    }
                }
                else
                {
                    if (p.V < th.RiseV - th.HysteresisV)
                    {
                        // Excursion ended before qualifying -> glitch, stay Off.
                        AbortQualification(p.T, p.T);
                    }
                    else if (p.T - qualStart >= th.MinStableNs)
                    {
                        qualifying = false;
                        CloseWindow(qualStart, "rise-qualified");
                        state = RailState.Good;
                        winStart = qualStart;
                        events.Add(new AnalysisEvent("transition", series.Rail, qualStart, 0, "rise",
                            new JsonObject(Basis()) { ["from"] = "Off", ["to"] = "Good" }));
                    }
                }
            }
            else if (state == RailState.Good)
            {
                if (!qualifying)
                {
                    if (p.V <= th.FallV)
                    {
                        qualifying = true;
                        qualDir = "fall";
                        qualStart = p.T;
                    }
                }
                else
                {
                    if (p.V > th.FallV + th.HysteresisV)
                    {
                        // Brief undervoltage shorter than min-stable -> glitch, not violation.
                        AbortQualification(p.T, p.T);
                    }
                    else if (p.T - qualStart >= th.MinStableNs)
                    {
                        qualifying = false;
                        var dur = p.T - qualStart;
                        events.Add(new AnalysisEvent("violation", series.Rail, qualStart, dur, "fall",
                            new JsonObject(Basis()) { ["kind"] = "undervoltage" }));
                        CloseWindow(qualStart, "fall-qualified");
                        state = RailState.Off;
                        winStart = qualStart;
                        events.Add(new AnalysisEvent("transition", series.Rail, qualStart, 0, "fall",
                            new JsonObject(Basis()) { ["from"] = "Good", ["to"] = "Off" }));
                    }
                }
            }
        }

        if (qualifying)
        {
            // Ran out of samples mid-excursion: cannot decide glitch vs violation.
            events.Add(new AnalysisEvent("unknown-region", series.Rail, qualStart, 0, qualDir,
                new JsonObject(Basis()) { ["reason"] = "truncated-excursion" }));
            qualifying = false;
        }

        var lastT = pts.Count > 0 ? pts[^1].T : winStart;
        CloseWindow(lastT, "end-of-data");
        return new RailAnalysis(series.Rail, Merge(windows), events);
    }

    private static List<StateWindow> Merge(List<StateWindow> ws)
    {
        var outp = new List<StateWindow>();
        foreach (var w in ws)
        {
            if (outp.Count > 0 && outp[^1].State == w.State && Math.Abs(outp[^1].TEnd - w.TStart) < 1e-9)
                outp[^1] = outp[^1] with { TEnd = w.TEnd, Reason = w.Reason };
            else
                outp.Add(w);
        }
        return outp;
    }

    /// <summary>First time the rail entered Good, and first time it left Good after that.</summary>
    public static (double? RiseT, double? FallT) KeyTimes(RailAnalysis a)
    {
        double? rise = null, fall = null;
        foreach (var e in a.Events)
        {
            if (e.Type != "transition") continue;
            if (e.Direction == "rise" && rise is null) rise = e.TStart;
            if (e.Direction == "fall" && rise is not null && fall is null && e.TStart > rise)
                fall = e.TStart;
        }
        return (rise, fall);
    }
}

/// <summary>
/// dep-v1: checks rise-after edges against measured Good windows, checks shutdown
/// as the exact reverse order, treats simultaneous arrivals (within clock
/// tolerance) as Unknown with all legal candidates kept.
/// </summary>
public static class DependencyChecker
{
    public static DependencyReport Check(
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyDictionary<string, RailAnalysis> rails,
        ClockSpec clock,
        string dependencyVersionId)
    {
        var tol = clock.JitterNs + clock.PeriodNs / 2.0;
        var verdicts = new List<EdgeVerdict>();

        foreach (var edge in edges)
        {
            if (edge.Kind != "rise-after")
            {
                verdicts.Add(new EdgeVerdict(edge, EdgeStatus.Unknown,
                    $"unsupported edge kind '{edge.Kind}'", null, null,
                    new List<string> { "treat-as-rise-after", "ignore-edge" }));
                continue;
            }

            if (!rails.TryGetValue(edge.From, out var a) || !rails.TryGetValue(edge.To, out var b))
            {
                verdicts.Add(new EdgeVerdict(edge, EdgeStatus.Unknown,
                    "missing rail data", null, null, new List<string> { "await-import" }));
                continue;
            }

            var (riseA, fallA) = WindowAnalyzer.KeyTimes(a);
            var (riseB, fallB) = WindowAnalyzer.KeyTimes(b);

            // Startup: To must rise after From is Good.
            EdgeStatus startup;
            string? evidence = null;
            var candidates = new List<string>();
            if (riseA is null || riseB is null)
            {
                startup = EdgeStatus.Unknown;
                evidence = "missing rise transition";
                candidates.Add("rail-never-rose");
            }
            else if (Math.Abs(riseA.Value - riseB.Value) <= tol)
            {
                startup = EdgeStatus.Unknown;
                evidence = $"simultaneous arrival within {tol}ns tolerance";
                candidates.Add($"{edge.From}-before-{edge.To}");
                candidates.Add($"{edge.To}-before-{edge.From}");
            }
            else if (riseB > riseA)
            {
                startup = EdgeStatus.Satisfied;
                if (edge.MaxDelayNs is { } max && riseB - riseA > max)
                {
                    startup = EdgeStatus.Violated;
                    evidence = $"delay {riseB - riseA}ns exceeds max {max}ns";
                }
            }
            else
            {
                startup = EdgeStatus.Violated;
                evidence = $"{edge.To} rose at {riseB}ns before {edge.From} was Good at {riseA}ns";
            }

            // Shutdown: exact reverse — From must not fall before To.
            if (startup != EdgeStatus.Violated && fallA is not null && fallB is not null)
            {
                if (Math.Abs(fallA.Value - fallB.Value) <= tol)
                {
                    startup = EdgeStatus.Unknown;
                    evidence = $"simultaneous shutdown within {tol}ns tolerance";
                    if (!candidates.Contains($"{edge.To}-falls-before-{edge.From}"))
                        candidates.Add($"{edge.To}-falls-before-{edge.From}");
                    candidates.Add($"{edge.From}-falls-before-{edge.To}");
                }
                else if (fallA < fallB)
                {
                    startup = EdgeStatus.Violated;
                    evidence = $"shutdown reverse-order violated: {edge.From} fell at {fallA}ns before {edge.To} fell at {fallB}ns";
                }
            }

            verdicts.Add(new EdgeVerdict(edge, startup, evidence, riseA, riseB, candidates));
        }

        var cycles = CycleFinder.ElementaryCycles(edges);
        var minSets = CycleFinder.MinimumFeedbackEdgeSets(edges);
        var first = verdicts
            .Where(v => v.Status == EdgeStatus.Violated)
            .OrderBy(v => v.TTo ?? double.MaxValue)
            .FirstOrDefault();
        return new DependencyReport(verdicts, minSets, cycles, first);
    }
}

/// <summary>
/// cycle-v1: finds elementary cycles and every minimum feedback edge set
/// (the minimal contradiction edge sets). When several minimum sets exist,
/// all of them are kept as legal candidates.
/// </summary>
public static class CycleFinder
{
    public static List<List<string>> ElementaryCycles(IReadOnlyList<DependencyEdge> edges)
    {
        var adj = edges.GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList());
        var cycles = new HashSet<string>();
        foreach (var start in adj.Keys)
            Dfs(start, start, new List<string> { start }, adj, cycles);
        return cycles.Select(c => (List<string>)System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(c)!).ToList();
    }

    private static void Dfs(string start, string node, List<string> path,
        Dictionary<string, List<string>> adj, HashSet<string> acc)
    {
        if (!adj.TryGetValue(node, out var nexts)) return;
        foreach (var next in nexts)
        {
            if (next == start && path.Count > 1)
            {
                var cyc = new List<string>(path);
                // Canonical rotation so the same cycle is recorded once.
                var minIdx = cyc.IndexOf(cyc.Min()!);
                var rot = cyc.Skip(minIdx).Concat(cyc.Take(minIdx)).ToList();
                acc.Add(System.Text.Json.JsonSerializer.Serialize(rot));
            }
            else if (!path.Contains(next) && string.CompareOrdinal(next, start) >= 0)
            {
                path.Add(next);
                Dfs(start, next, path, adj, acc);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>All minimum-size edge sets whose removal makes the graph acyclic.</summary>
    public static List<List<DependencyEdge>> MinimumFeedbackEdgeSets(IReadOnlyList<DependencyEdge> edges)
    {
        if (ElementaryCycles(edges).Count == 0)
            return new List<List<DependencyEdge>>();
        var list = edges.ToList();
        for (var k = 1; k <= list.Count; k++)
        {
            var found = new List<List<DependencyEdge>>();
            foreach (var combo in Combinations(list.Count, k))
            {
                var removed = combo.Select(i => list[i]).ToHashSet();
                var rest = list.Where(e => !removed.Contains(e)).ToList();
                if (ElementaryCycles(rest).Count == 0)
                    found.Add(combo.Select(i => list[i]).ToList());
            }
            if (found.Count > 0)
                return found;
        }
        return new List<List<DependencyEdge>>();
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
