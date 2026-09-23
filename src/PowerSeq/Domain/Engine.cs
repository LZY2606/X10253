namespace PowerSeq.Domain;

public record OrderCandidate(string Label, string Explanation);

public record OrderFinding(
    string Kind,          // startup-order | shutdown-order
    string Before,
    string After,
    string Status,        // ok | violation | undecided | unknown
    IReadOnlyList<OrderCandidate> Candidates,
    string Reason);

public record ComputeResult(
    IReadOnlyList<RailAnalysis> Rails,
    IReadOnlyList<OrderFinding> OrderFindings,
    CycleReport Cycles,
    IReadOnlyList<string> UsedRails,
    string AlgorithmVersion,
    string ParamFingerprint);

/// <summary>
/// Combines per-rail analyses with the dependency graph. Simultaneous arrivals
/// (crossing times within combined measurement error) stay undecided and keep
/// every legal candidate ordering as first-class data.
/// </summary>
public static class Engine
{
    public static ComputeResult Compute(
        IReadOnlyList<RailWaveform> waveforms,
        ConstraintSet constraints)
    {
        var analyses = new List<RailAnalysis>();
        var byRail = new Dictionary<string, RailWaveform>(StringComparer.OrdinalIgnoreCase);
        foreach (var wf in waveforms) byRail[wf.Rail] = wf;

        foreach (var wf in waveforms)
        {
            if (constraints.Thresholds.TryGetValue(wf.Rail, out var th))
                analyses.Add(Sequencer.Analyze(wf, th));
        }
        var analysisByRail = analyses.ToDictionary(a => a.Rail, StringComparer.OrdinalIgnoreCase);

        var findings = new List<OrderFinding>();
        foreach (var edge in constraints.Edges)
        {
            analysisByRail.TryGetValue(edge.Before, out var a);
            analysisByRail.TryGetValue(edge.After, out var b);
            byRail.TryGetValue(edge.Before, out var wa);
            byRail.TryGetValue(edge.After, out var wb);
            var margin = (wa?.MeasurementErrorMs ?? 0) + (wb?.MeasurementErrorMs ?? 0);

            // startup: before must reach GOOD no later than after
            if (a?.GoodAtMs is null || b?.GoodAtMs is null)
            {
                findings.Add(new OrderFinding("startup-order", edge.Before, edge.After, "unknown",
                    new List<OrderCandidate>(), "缺少 good 时刻（波形缺失、单位无法转换或时钟缺口）"));
            }
            else
            {
                var diff = b.GoodAtMs.Value - a.GoodAtMs.Value; // >=0 means order ok
                if (Math.Abs(diff) <= margin)
                {
                    findings.Add(new OrderFinding("startup-order", edge.Before, edge.After, "undecided",
                        new List<OrderCandidate>
                        {
                            new($"{edge.Before}-first", $"{edge.Before} 先到达 good"),
                            new($"{edge.After}-first", $"{edge.After} 先到达 good"),
                            new("simultaneous", "两者同时到达"),
                        },
                        $"同时到达: |Δt|={Math.Abs(diff):F3} ms ≤ 合成测量误差 {margin:F3} ms，无法唯一决定，保留全部合法候选"));
                }
                else if (diff < 0)
                {
                    findings.Add(new OrderFinding("startup-order", edge.Before, edge.After, "violation",
                        new List<OrderCandidate>(),
                        $"启动顺序违约: {edge.After} 在 {b.GoodAtMs:F3} ms 达到 good，先于 {edge.Before} 的 {a.GoodAtMs:F3} ms（超出误差 {margin:F3} ms）"));
                }
                else
                {
                    findings.Add(new OrderFinding("startup-order", edge.Before, edge.After, "ok",
                        new List<OrderCandidate>(), $"启动顺序满足: Δt={diff:F3} ms"));
                }
            }

            // shutdown: reverse order required — after must turn off no later than before
            if (constraints.ShutdownReverse)
            {
                if (a?.OffAtMs is null || b?.OffAtMs is null)
                {
                    findings.Add(new OrderFinding("shutdown-order", edge.Before, edge.After, "unknown",
                        new List<OrderCandidate>(), "缺少 off 时刻（未捕获关机或时钟缺口）"));
                }
                else
                {
                    var diff = a.OffAtMs.Value - b.OffAtMs.Value; // >=0 means reverse order ok
                    if (Math.Abs(diff) <= margin)
                    {
                        findings.Add(new OrderFinding("shutdown-order", edge.Before, edge.After, "undecided",
                            new List<OrderCandidate>
                            {
                                new($"{edge.After}-off-first", $"{edge.After} 先关断（符合逆序）"),
                                new($"{edge.Before}-off-first", $"{edge.Before} 先关断（逆序违约）"),
                                new("simultaneous", "两者同时关断"),
                            },
                            $"关机同时到达: |Δt|={Math.Abs(diff):F3} ms ≤ 合成测量误差 {margin:F3} ms"));
                    }
                    else if (diff < 0)
                    {
                        findings.Add(new OrderFinding("shutdown-order", edge.Before, edge.After, "violation",
                            new List<OrderCandidate>(),
                            $"关机逆序违约: {edge.Before} 在 {a.OffAtMs:F3} ms 关断，先于依赖它的 {edge.After} 的 {b.OffAtMs:F3} ms"));
                    }
                    else
                    {
                        findings.Add(new OrderFinding("shutdown-order", edge.Before, edge.After, "ok",
                            new List<OrderCandidate>(), $"关机逆序满足: Δt={diff:F3} ms"));
                    }
                }
            }
        }

        var usedRails = constraints.Edges
            .SelectMany(e => new[] { e.Before, e.After })
            .Concat(constraints.Thresholds.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ComputeResult(analyses, findings, DependencyGraph.AnalyzeCycles(constraints.Edges),
            usedRails, Sequencer.AlgorithmVersion, constraints.Fingerprint);
    }
}
