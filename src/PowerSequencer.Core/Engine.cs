namespace PowerSequencer.Core;

/// <summary>Unit normalization, applied only in the derived layer.
/// Unknown units return null so the raw value stays in place with a diagnostic.</summary>
public static class Units
{
    public static double? ToUs(double value, string unit) => unit switch
    {
        "us" => value,
        "ms" => value * 1000.0,
        "ns" => value / 1000.0,
        _ => null,
    };

    public static double? ToV(double value, string unit) => unit switch
    {
        "V" => value,
        "mV" => value / 1000.0,
        _ => null,
    };
}

public static class Engine
{
    /// <summary>Hysteresis state machine + min-stable-time classification.
    /// Rising crossings use RiseV, falling crossings use FallV (different thresholds).
    /// A short excursion is a glitch when its duration is below MinStableUs,
    /// otherwise it is a violation. Clock gaps produce Unknown windows.</summary>
    public static WindowPayload EvaluateWindows(
        Waveform wf, RailThreshold th, ClockSpec clock, ErrorSpec err)
    {
        var diagnostics = new List<string>();
        var pts = new List<(double t, double v)>();
        foreach (var p in wf.Points)
        {
            var t = Units.ToUs(p.Time, wf.TimeUnit);
            var v = Units.ToV(p.Voltage, wf.VoltageUnit);
            if (t is null || v is null)
            {
                diagnostics.Add($"无法转换采样点 ({p.RawTime} {wf.TimeUnit}, {p.RawVoltage} {wf.VoltageUnit})，原始值保留，不参与派生计算");
                continue;
            }
            pts.Add((t.Value, v.Value));
        }
        foreach (var d in wf.Diagnostics)
            diagnostics.Add($"第{(d.Line?.ToString() ?? "?")}行: {d.Message} — 原始内容: {d.Raw}");

        var judgments = new List<Judgment>
        {
            new("rise-fall-thresholds",
                $"上升阈值 {th.RiseV}V 与下降阈值 {th.FallV}V 不同，滞回 {th.HysteresisV}V",
                new() { ["riseV"] = th.RiseV.ToString("R"), ["fallV"] = th.FallV.ToString("R"), ["hysteresisV"] = th.HysteresisV.ToString("R") }),
        };

        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();
        if (pts.Count == 0)
        {
            windows.Add(new StateWindow("unknown", 0, 0, "无可用采样点"));
            return new WindowPayload(wf.Rail, windows, excursions, judgments, diagnostics);
        }

        // 1) hysteresis-filtered raw state per sample (0 = not good, 1 = good)
        var state = new int[pts.Count];
        int cur = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            if (cur == 0 && pts[i].v >= th.RiseV) cur = 1;
            else if (cur == 1 && pts[i].v <= th.FallV) cur = 0;
            state[i] = cur;
        }

        // 2) constant-state intervals [t_i, t_{i+1})
        var intervals = new List<(int s, double from, double to)>();
        for (int i = 0; i < pts.Count; i++)
        {
            double from = pts[i].t;
            double to = i + 1 < pts.Count ? pts[i + 1].t : pts[i].t + clock.NominalPeriodUs;
            if (intervals.Count > 0 && intervals[^1].s == state[i])
                intervals[^1] = (intervals[^1].s, intervals[^1].from, to);
            else
                intervals.Add((state[i], from, to));
        }

        // 3) classify short opposite-state intervals as glitch vs violation by duration
        var confirmed = new List<(int s, double from, double to)>();
        for (int i = 0; i < intervals.Count; i++)
        {
            var iv = intervals[i];
            bool interior = i > 0 && i < intervals.Count - 1;
            double dur = iv.to - iv.from;
            if (interior && dur < th.MinStableUs)
            {
                var kind = iv.s == 0 ? "dip" : "blip";
                var id = Ids.Of("ex", $"{wf.Rail}|{iv.from:R}|{iv.to:R}|{kind}");
                excursions.Add(new Excursion(id, wf.Rail, kind, iv.from, iv.to, dur, "glitch",
                    $"持续时间 {dur:R}us < 最小稳定时间 {th.MinStableUs:R}us，判定为 glitch 而非违约"));
                judgments.Add(new Judgment("glitch-vs-violation",
                    $"越界持续 {dur:R}us < {th.MinStableUs:R}us → glitch",
                    new() { ["durationUs"] = dur.ToString("R"), ["minStableUs"] = th.MinStableUs.ToString("R"), ["verdict"] = "glitch", ["excursion"] = id }));
                continue; // absorbed into surrounding confirmed state
            }
            if (interior && iv.s == 0 && confirmed.Count > 0 && confirmed[^1].s == 1)
            {
                var id = Ids.Of("ex", $"{wf.Rail}|{iv.from:R}|{iv.to:R}|dip");
                excursions.Add(new Excursion(id, wf.Rail, "dip", iv.from, iv.to, dur, "violation",
                    $"掉电持续 {dur:R}us ≥ 最小稳定时间 {th.MinStableUs:R}us，判定为违约"));
                judgments.Add(new Judgment("glitch-vs-violation",
                    $"越界持续 {dur:R}us ≥ {th.MinStableUs:R}us → violation",
                    new() { ["durationUs"] = dur.ToString("R"), ["minStableUs"] = th.MinStableUs.ToString("R"), ["verdict"] = "violation", ["excursion"] = id }));
            }
            confirmed.Add(iv);
        }

        // 4) merge adjacent confirmed intervals of equal state
        var merged = new List<(int s, double from, double to)>();
        foreach (var iv in confirmed)
        {
            if (merged.Count > 0 && merged[^1].s == iv.s && merged[^1].to >= iv.from - 1e-9)
                merged[^1] = (merged[^1].s, merged[^1].from, iv.to);
            else
                merged.Add(iv);
        }
        foreach (var iv in merged)
            windows.Add(new StateWindow(iv.s == 1 ? "good" : "bad", iv.from, iv.to,
                $"滞回状态机（rise={th.RiseV}V, fall={th.FallV}V）"));

        // 5) clock gaps overlay unknown windows
        double maxGap = clock.NominalPeriodUs * clock.MaxGapFactor;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double gap = pts[i + 1].t - pts[i].t;
            if (gap > maxGap)
            {
                windows.Add(new StateWindow("unknown", pts[i].t, pts[i + 1].t,
                    $"采样缺口 {gap:R}us > 允许上限 {maxGap:R}us（{clock.NominalPeriodUs:R}us × {clock.MaxGapFactor:R}）"));
                judgments.Add(new Judgment("clock-gap",
                    $"采样缺口 {gap:R}us，窗口状态未知",
                    new() { ["gapUs"] = gap.ToString("R"), ["maxGapUs"] = maxGap.ToString("R") }));
            }
        }

        windows.Sort((a, b) => a.FromUs.CompareTo(b.FromUs));
        return new WindowPayload(wf.Rail, windows, excursions, judgments, diagnostics);
    }

    public static double? FirstStateChange(WindowPayload w, string toState, double afterUs = double.NegativeInfinity)
    {
        foreach (var win in w.Windows.Where(x => x.State == toState).OrderBy(x => x.FromUs))
            if (win.FromUs >= afterUs) return win.FromUs;
        return null;
    }

    /// <summary>Startup/shutdown ordering with measurement tolerance.
    /// Arrivals within tolerance are simultaneous: every legal order is kept as a candidate.
    /// Shutdown must be the reverse of startup.</summary>
    public static SeqPayload AnalyzeSequence(
        IReadOnlyDictionary<string, WindowPayload> windows,
        DependencyGraphInput deps, ErrorSpec err)
    {
        var goodAt = new Dictionary<string, double?>();
        var offAt = new Dictionary<string, double?>();
        foreach (var (rail, w) in windows)
        {
            goodAt[rail] = FirstStateChange(w, "good");
            var g = goodAt[rail];
            offAt[rail] = g is null ? null : FirstStateChange(w, "bad", g.Value + 1e-9);
        }

        var checks = new List<EdgeCheck>();
        var judgments = new List<Judgment>();
        var violations = new List<Violation>();

        foreach (var e in deps.Edges)
        {
            var ga = goodAt.GetValueOrDefault(e.Before);
            var gb = goodAt.GetValueOrDefault(e.After);
            if (ga is null || gb is null)
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "startup", null, err.TimeTolUs,
                    "unknown", new(), "缺少 good 时间（时钟缺口或未达到阈值），无法判定"));
                continue;
            }
            double diff = gb.Value - ga.Value; // expect After good later than Before
            if (Math.Abs(diff) <= err.TimeTolUs)
            {
                var cands = new List<string> { $"{e.Before}先于{e.After}", $"{e.After}先于{e.Before}" };
                checks.Add(new EdgeCheck(e.Before, e.After, "startup", diff, err.TimeTolUs,
                    "simultaneous", cands, $"到达时间差 {diff:R}us 在测量误差 ±{err.TimeTolUs:R}us 内，无法唯一决定顺序"));
                judgments.Add(new Judgment("simultaneous-arrival",
                    $"{e.Before}/{e.After} 同时到达，保留全部合法候选",
                    new() { ["diffUs"] = diff.ToString("R"), ["tolUs"] = err.TimeTolUs.ToString("R") }));
            }
            else if (diff < 0)
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "startup", diff, err.TimeTolUs,
                    "violation", new(), $"{e.After} 先于 {e.Before} 达到 good，差 {diff:R}us"));
                violations.Add(new Violation("startup-order", Math.Min(ga.Value, gb.Value),
                    $"启动顺序违约：{e.After} 在 {e.Before} 之前达到 good（差 {diff:R}us）",
                    new() { e.Before, e.After }));
            }
            else
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "startup", diff, err.TimeTolUs,
                    "ok", new(), $"{e.Before} 先于 {e.After} {diff:R}us"));
            }

            // shutdown: reverse of startup — After must go off before Before
            var oa = offAt.GetValueOrDefault(e.Before);
            var ob = offAt.GetValueOrDefault(e.After);
            if (oa is null || ob is null)
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "shutdown", null, err.TimeTolUs,
                    "unknown", new(), "缺少关机时间，无法判定"));
                continue;
            }
            double sdiff = oa.Value - ob.Value; // expect Before off later than After
            if (Math.Abs(sdiff) <= err.TimeTolUs)
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "shutdown", sdiff, err.TimeTolUs,
                    "simultaneous", new() { $"{e.After}先关", $"{e.Before}先关" },
                    $"关机时间差 {sdiff:R}us 在测量误差内"));
            }
            else if (sdiff < 0)
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "shutdown", sdiff, err.TimeTolUs,
                    "violation", new(), $"关机逆序违约：{e.Before} 先于 {e.After} 关闭（差 {sdiff:R}us）"));
                violations.Add(new Violation("shutdown-reverse-order", Math.Min(oa.Value, ob.Value),
                    $"关机逆序违约：{e.Before} 应先于 {e.After} 之后关闭，实际提前 {(-sdiff):R}us",
                    new() { e.Before, e.After }));
            }
            else
            {
                checks.Add(new EdgeCheck(e.Before, e.After, "shutdown", sdiff, err.TimeTolUs,
                    "ok", new(), $"关机顺序正确（{e.After} 先关 {sdiff:R}us）"));
            }
        }

        var first = violations.OrderBy(v => v.TimeUs).FirstOrDefault();
        if (first is not null)
            judgments.Add(new Judgment("first-violation", first.Summary,
                new() { ["rule"] = first.Rule, ["timeUs"] = first.TimeUs.ToString("R") }));

        return new SeqPayload(goodAt, offAt, checks, first, judgments);
    }

    /// <summary>Finds all elementary cycles and every minimum-size feedback edge set.
    /// When the minimum set is not unique, all legal candidates are kept.</summary>
    public static CyclePayload CheckCycles(DependencyGraphInput deps)
    {
        var edges = deps.Edges;
        var cycles = FindCycles(edges);
        var minSets = new List<List<DepEdge>>();
        if (cycles.Count > 0)
        {
            for (int size = 1; size <= edges.Count && minSets.Count == 0; size++)
                foreach (var combo in Combinations(edges, size))
                    if (!HasCycle(edges.Except(combo).ToList()))
                        minSets.Add(combo);
        }
        var judgments = new List<Judgment>();
        if (cycles.Count > 0)
            judgments.Add(new Judgment("cycle-min-edge-set",
                $"检测到 {cycles.Count} 个循环，最小矛盾边集大小 {minSets.First().Count}，共 {minSets.Count} 个合法候选",
                new()
                {
                    ["cycles"] = cycles.Count.ToString(),
                    ["minSetSize"] = minSets.First().Count.ToString(),
                    ["candidates"] = string.Join(" | ", minSets.Select(s => string.Join(",", s.Select(e => $"{e.Before}->{e.After}")))),
                }));
        return new CyclePayload(cycles, minSets, judgments);
    }

    private static List<List<DepEdge>> FindCycles(List<DepEdge> edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Before, e.After }).Distinct().ToList();
        var cycles = new List<List<DepEdge>>();
        var seen = new HashSet<string>();
        foreach (var start in nodes)
            Dfs(start, start, new List<DepEdge>(), new HashSet<string>());
        void Dfs(string start, string node, List<DepEdge> path, HashSet<string> visited)
        {
            foreach (var e in edges.Where(x => x.Before == node))
            {
                if (e.After == start && path.Count >= 1)
                {
                    var cyc = path.Concat(new[] { e }).ToList();
                    var key = CanonicalCycleKey(cyc);
                    if (seen.Add(key)) cycles.Add(cyc);
                }
                else if (!visited.Contains(e.After) && path.Count < nodes.Count)
                {
                    visited.Add(e.After);
                    path.Add(e);
                    Dfs(start, e.After, path, visited);
                    path.RemoveAt(path.Count - 1);
                    visited.Remove(e.After);
                }
            }
        }
        return cycles;
    }

    private static string CanonicalCycleKey(List<DepEdge> cyc)
    {
        var reps = cyc.Select(e => $"{e.Before}->{e.After}").ToList();
        var best = "";
        for (int r = 0; r < reps.Count; r++)
        {
            var rotated = string.Join(";", reps.Skip(r).Concat(reps.Take(r)));
            if (best == "" || string.CompareOrdinal(rotated, best) < 0) best = rotated;
        }
        return best;
    }

    private static bool HasCycle(List<DepEdge> edges)
    {
        var indeg = new Dictionary<string, int>();
        foreach (var e in edges)
        {
            indeg.TryGetValue(e.Before, out _);
            indeg[e.After] = indeg.GetValueOrDefault(e.After) + 1;
            indeg.TryGetValue(e.Before, out var b);
            indeg[e.Before] = b;
        }
        var queue = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        int removed = 0;
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            removed++;
            foreach (var e in edges.Where(x => x.Before == n))
                if (--indeg[e.After] == 0) queue.Enqueue(e.After);
        }
        return removed < indeg.Count;
    }

    private static IEnumerable<List<DepEdge>> Combinations(List<DepEdge> edges, int size)
    {
        var idx = new int[size];
        for (int i = 0; i < size; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => edges[i]).ToList();
            int p = size - 1;
            while (p >= 0 && idx[p] == edges.Count - size + p) p--;
            if (p < 0) yield break;
            idx[p]++;
            for (int i = p + 1; i < size; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
