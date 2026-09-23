namespace EvidenceRoom;

/// <summary>Derived-layer analysis. Pure functions of (inputs, params) so results are replayable.</summary>
public static class Engine
{
    public const string AlgoVersion = "algo/1.0";

    // ---------- rail status windows (hysteresis state machine) ----------

    public sealed record Window(string State, double Start, double End); // state: low|high|unknown
    public sealed record RailEvent(
        string Kind,        // glitch | state-change | clock-gap | unknown-region
        double At, double Duration, string Detail,
        string Basis);      // machine-readable judgement basis
    public sealed record RailAnalysis(
        string Rail,
        List<Window> Windows,
        List<RailEvent> Events,
        List<string> Diagnostics,
        double? HighAt,     // first sustained high start
        double? LowAt);     // first sustained low after high

    public static RailAnalysis AnalyzeRail(
        RailWaveform rail, RailThreshold th, double clockInterval, double voltErr)
    {
        var rise = th.Rise.Normalized;
        var fall = th.Fall.Normalized;
        var minStable = th.MinStable.Normalized;
        var diags = new List<string>();
        foreach (var q in new[] { th.Rise, th.Fall, th.MinStable })
            if (q.Diagnostic is not null) diags.Add($"{rail.Name}: {q.Raw} -> {q.Diagnostic}");
        var windows = new List<Window>();
        var events = new List<RailEvent>();

        var pts = rail.Samples
            .Select(s => (t: s.T.Normalized, v: s.V.Normalized, rawT: s.T.Raw, rawV: s.V.Raw))
            .ToList();
        foreach (var p in pts.Where(p => p.t is null || p.v is null))
            diags.Add($"{rail.Name}: 样本 ({p.rawT},{p.rawV}) 保留在原位置，无法规范化，不参与派生计算");
        var usable = pts.Where(p => p.t is not null && p.v is not null)
            .Select(p => (t: p.t!.Value, v: p.v!.Value)).OrderBy(p => p.t).ToList();

        if (rise is null || fall is null || minStable is null || usable.Count == 0)
        {
            diags.Add($"{rail.Name}: 阈值或样本不可用，状态窗口未知");
            return new RailAnalysis(rail.Name, windows, events, diags, null, null);
        }

        var gapLimit = 1.5 * clockInterval;
        var state = "low";
        var winStart = usable[0].t;
        double? pendingSince = null;
        double? highAt = null, lowAt = null;
        var seenHigh = false;

        void CloseWindow(double end)
        {
            if (end > winStart || windows.Count == 0)
                windows.Add(new Window(state, winStart, Math.Max(end, winStart)));
        }

        for (var i = 0; i < usable.Count; i++)
        {
            var (t, v) = usable[i];
            if (i > 0)
            {
                var dt = t - usable[i - 1].t;
                if (dt > gapLimit)
                {
                    CloseWindow(usable[i - 1].t);
                    events.Add(new RailEvent("clock-gap", usable[i - 1].t, dt,
                        $"采样时钟缺口 {dt * 1000:0.###}ms > 1.5×{clockInterval * 1000:0.###}ms",
                        "gap-duration>1.5*clock-interval"));
                    windows.Add(new Window("unknown", usable[i - 1].t, t));
                    events.Add(new RailEvent("unknown-region", usable[i - 1].t, dt,
                        "时钟缺口期间轨压状态未知", "state=unknown during clock gap"));
                    winStart = t;
                    pendingSince = null;
                    // state persists across the gap but is re-anchored on next samples
                }
            }

            if (state == "low" && v >= rise + voltErr * 0) // threshold compare uses raw value; err recorded separately
            {
                pendingSince ??= t;
                if (t - pendingSince.Value >= minStable - 1e-12)
                {
                    CloseWindow(pendingSince.Value);
                    state = "high";
                    winStart = pendingSince.Value;
                    events.Add(new RailEvent("state-change", pendingSince.Value, t - pendingSince.Value,
                        $"low->high 在 {pendingSince.Value * 1000:0.###}ms 生效（持续 {(t - pendingSince.Value) * 1000:0.###}ms ≥ 最小稳定 {minStable * 1000:0.###}ms）",
                        "duration>=min-stable => state-change"));
                    highAt ??= pendingSince.Value;
                    seenHigh = true;
                    pendingSince = null;
                }
            }
            else if (state == "low" && pendingSince is not null && v < rise)
            {
                var dur = t - pendingSince.Value;
                events.Add(new RailEvent("glitch", pendingSince.Value, dur,
                    $"上升阈值上方短暂停留 {dur * 1000:0.###}ms < 最小稳定 {minStable * 1000:0.###}ms，判为 glitch 而非违约",
                    "duration<min-stable => glitch"));
                pendingSince = null;
            }
            else if (state == "high" && v <= fall)
            {
                pendingSince ??= t;
                if (t - pendingSince.Value >= minStable - 1e-12)
                {
                    CloseWindow(pendingSince.Value);
                    state = "low";
                    winStart = pendingSince.Value;
                    events.Add(new RailEvent("state-change", pendingSince.Value, t - pendingSince.Value,
                        $"high->low 在 {pendingSince.Value * 1000:0.###}ms 生效（持续 {(t - pendingSince.Value) * 1000:0.###}ms ≥ 最小稳定）",
                        "duration>=min-stable => state-change"));
                    if (seenHigh && lowAt is null) lowAt = pendingSince.Value;
                    pendingSince = null;
                }
            }
            else if (state == "high" && pendingSince is not null && v > fall)
            {
                var dur = t - pendingSince.Value;
                events.Add(new RailEvent("glitch", pendingSince.Value, dur,
                    $"下降阈值下方短暂越界 {dur * 1000:0.###}ms < 最小稳定 {minStable * 1000:0.###}ms，判为 glitch 而非违约",
                    "duration<min-stable => glitch"));
                pendingSince = null;
            }
        }
        CloseWindow(usable[^1].t);
        return new RailAnalysis(rail.Name, windows, events, diags, highAt, lowAt);
    }

    // ---------- sequence checks ----------

    public sealed record Candidate(string Label, string Status, string Detail, string Basis);
    // Status: violation | ok | undetermined | unknown

    public sealed record SequenceReport(
        List<Candidate> Candidates,
        Candidate? FirstViolation);

    public static SequenceReport CheckStartup(
        IReadOnlyDictionary<string, RailAnalysis> rails,
        IReadOnlyList<DependencyEdge> edges,
        double clockInterval)
    {
        var tol = clockInterval / 2.0;
        var cands = new List<Candidate>();
        foreach (var e in edges.Where(e => e.Phase is "startup" or "both"))
        {
            if (!rails.TryGetValue(e.From, out var a) || !rails.TryGetValue(e.To, out var b))
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "unknown",
                    "依赖边引用了不存在的轨", "missing-rail => unknown"));
                continue;
            }
            var minDelay = e.MinDelay.Normalized ?? 0;
            if (a.HighAt is null || b.HighAt is null)
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "unknown",
                    $"无法唯一决定：{(a.HighAt is null ? e.From : e.To)} 从未达到稳定高电平",
                    "missing-good-window => unknown"));
                continue;
            }
            var delta = b.HighAt.Value - a.HighAt.Value;
            var need = minDelay;
            if (delta >= need - 1e-12)
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "ok",
                    $"{e.To} 滞后 {e.From} {delta * 1000:0.###}ms ≥ 最小延迟 {need * 1000:0.###}ms",
                    "delta>=min-delay => ok"));
            }
            else if (Math.Abs(delta - need) <= tol || Math.Abs(delta) <= tol)
            {
                // simultaneous arrival: keep every legal candidate
                cands.Add(new Candidate($"{e.From}->{e.To}", "undetermined",
                    $"同时到达：|Δ|={Math.Abs(delta) * 1000:0.###}ms ≤ 采样容差 {tol * 1000:0.###}ms，" +
                    $"合法候选=[{e.From}先稳定, {e.To}先稳定, 二者同时]，无法唯一决定",
                    "|delta|<=clock-tolerance => undetermined; all legal candidates retained"));
            }
            else
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "violation",
                    $"{e.To} 在 {e.From} 稳定后 {delta * 1000:0.###}ms 即就绪，早于最小延迟 {need * 1000:0.###}ms",
                    "delta<min-delay-tolerance => violation"));
            }
        }
        var first = cands.Where(c => c.Status == "violation")
            .Cast<Candidate?>().FirstOrDefault();
        return new SequenceReport(cands, first);
    }

    public static SequenceReport CheckShutdown(
        IReadOnlyDictionary<string, RailAnalysis> rails,
        IReadOnlyList<DependencyEdge> edges,
        double clockInterval)
    {
        var tol = clockInterval / 2.0;
        var cands = new List<Candidate>();
        foreach (var e in edges.Where(e => e.Phase is "shutdown" or "both"))
        {
            if (!rails.TryGetValue(e.From, out var a) || !rails.TryGetValue(e.To, out var b))
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "unknown",
                    "依赖边引用了不存在的轨", "missing-rail => unknown"));
                continue;
            }
            if (a.LowAt is null || b.LowAt is null)
            {
                cands.Add(new Candidate($"{e.From}->{e.To}", "unknown",
                    "无法唯一决定：存在轨从未稳定关断", "missing-fall-window => unknown"));
                continue;
            }
            // startup: from before to  =>  shutdown: to must fall before (or with) from
            var delta = a.LowAt.Value - b.LowAt.Value; // >=0 means correct reverse order
            if (delta >= -tol)
                cands.Add(new Candidate($"{e.From}->{e.To}", "ok",
                    $"关机逆序正确：{e.To} 先于 {e.From} 关断（提前 {delta * 1000:0.###}ms）",
                    "fall-order-reversed-of-startup => ok"));
            else
                cands.Add(new Candidate($"{e.From}->{e.To}", "violation",
                    $"关机逆序违约：{e.From} 先于 {e.To} {(-delta) * 1000:0.###}ms 关断，违反启动顺序的逆序",
                    "fall-order-not-reversed => violation"));
        }
        var first = cands.Where(c => c.Status == "violation").Cast<Candidate?>().FirstOrDefault();
        return new SequenceReport(cands, first);
    }

    // ---------- cyclic dependencies: minimal conflicting edge sets ----------

    public sealed record CycleReport(
        bool HasCycle,
        List<List<string>> MinimalEdgeSets, // every minimum-size feedback edge set
        string Status);                     // ok | violation(unique) | undetermined(multiple)

    public static CycleReport CheckCycles(IReadOnlyList<DependencyEdge> edges)
    {
        var edgeList = edges.Select(e => $"{e.From}->{e.To}").ToList();
        bool Acyclic(IReadOnlyCollection<int> removed)
        {
            var adj = new Dictionary<string, List<string>>();
            for (var i = 0; i < edges.Count; i++)
            {
                if (removed.Contains(i)) continue;
                adj.TryAdd(edges[i].From, new()).Add(edges[i].To);
                adj.TryAdd(edges[i].To, new());
            }
            var color = new Dictionary<string, int>();
            var stack = new Stack<(string node, IEnumerator<string> it)>();
            foreach (var n in adj.Keys)
            {
                if (color.ContainsKey(n)) continue;
                color[n] = 1;
                stack.Push((n, adj[n].GetEnumerator()));
                while (stack.Count > 0)
                {
                    var (node, it) = stack.Peek();
                    if (it.MoveNext())
                    {
                        var next = it.Current;
                        if (!color.TryGetValue(next, out var c))
                        {
                            color[next] = 1;
                            stack.Push((next, adj[next].GetEnumerator()));
                        }
                        else if (c == 1) return false;
                    }
                    else { color[node] = 2; stack.Pop(); }
                }
            }
            return true;
        }

        if (Acyclic(new HashSet<int>()))
            return new CycleReport(false, new List<List<string>>(), "ok");

        // exact search for all minimum-size feedback edge sets (graphs here are small)
        for (var k = 1; k <= edgeList.Count; k++)
        {
            var found = new List<List<string>>();
            foreach (var combo in Combinations(edgeList.Count, k))
            {
                if (Acyclic(combo.ToHashSet()))
                    found.Add(combo.Select(i => edgeList[i]).ToList());
            }
            if (found.Count > 0)
                return new CycleReport(true, found, found.Count == 1 ? "violation" : "undetermined");
        }
        return new CycleReport(true, new List<List<string>> { edgeList }, "undetermined");
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.ToArray();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
