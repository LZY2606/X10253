namespace PowerSeq.Core;

/// <summary>
/// Offline case pack "电源次序证据室：轨压窗口、依赖约束与启动反例".
/// A hardware verification team imports multi-rail samples, enable/good signals and
/// versioned timing constraints, then reviews the power-up and shutdown sequences.
/// The script walks through: hysteresis, short glitch, simultaneous arrival, cyclic
/// dependency, shutdown reverse order, clock gap and threshold-version partial staleness.
/// </summary>
public static class CasePack
{
    public const string Name = "电源次序证据室：轨压窗口、依赖约束与启动反例";

    /// <summary>Sample a piecewise-linear rail at a fixed period; a gap range drops samples (clock gap).</summary>
    public static RailWaveform Rail(string rail, string role, double period, double errorMv,
        IReadOnlyList<(double t, double v)> knots, double t0, double t1,
        (double from, double to)? gap = null, string unit = "mV")
    {
        var samples = new List<SamplePoint>();
        for (var t = t0; t <= t1 + 1e-9; t += period)
        {
            t = Math.Round(t, 6);
            if (gap is { } g && t > g.from && t < g.to) continue;
            samples.Add(new SamplePoint(t, Math.Round(Interp(knots, t), 3)));
        }
        return new RailWaveform(rail, role, unit, "ms", period, errorMv, samples);
    }

    private static double Interp(IReadOnlyList<(double t, double v)> knots, double t)
    {
        if (t <= knots[0].t) return knots[0].v;
        for (int i = 1; i < knots.Count; i++)
            if (t <= knots[i].t)
            {
                var (t0, v0) = knots[i - 1];
                var (t1, v1) = knots[i];
                return v1 == v0 ? v0 : v0 + (v1 - v0) * (t - t0) / (t1 - t0);
            }
        return knots[^1].v;
    }

    public static WaveformSet InitialWaveforms(IClock clock, string? parentId = null, bool fillClockGap = false)
    {
        var rails = new List<RailWaveform>
        {
            // VDD_3V3: rises ~1.4 ms, falls ~9.1 ms. Clock gap 4.0..4.6 ms (filled in the local revision).
            Rail("VDD_3V3", "rail", 0.1, 12,
                new[] { (0.0, 0.0), (0.5, 0.0), (1.5, 3300.0), (8.5, 3300.0), (9.5, 0.0), (10.0, 0.0) },
                0.0, 10.0, gap: fillClockGap ? null : (4.0, 4.6)),
            // VDD_1V8: rises ~2.4 ms; 0.2 ms dip below fall threshold at 5.0 (glitch vs violation by duration);
            // noise inside the hysteresis band at 6.0..6.5 (no transitions); falls ~7.9 ms.
            Rail("VDD_1V8", "rail", 0.1, 9,
                new[] { (0.0, 0.0), (1.5, 0.0), (2.5, 1800.0), (4.9, 1800.0), (5.0, 1400.0), (5.2, 1400.0), (5.3, 1800.0),
                        (5.9, 1800.0), (6.0, 1580.0), (6.1, 1600.0), (6.2, 1560.0), (6.3, 1590.0), (6.4, 1570.0), (6.5, 1800.0),
                        (7.5, 1800.0), (8.2, 0.0), (10.0, 0.0) },
                0.0, 10.0),
            // VDD_0V9: rise crossing simultaneous with VDD_1V8 within tolerance.
            Rail("VDD_0V9", "rail", 0.1, 6,
                new[] { (0.0, 0.0), (1.9, 0.0), (2.44, 900.0), (6.8, 900.0), (7.4, 0.0), (10.0, 0.0) },
                0.0, 10.0),
            // VDD_1V2: falls AFTER its parent VDD_1V8 -> shutdown reverse-order violation.
            Rail("VDD_1V2", "rail", 0.1, 6,
                new[] { (0.0, 0.0), (2.8, 0.0), (3.3, 1200.0), (8.2, 1200.0), (8.7, 0.0), (10.0, 0.0) },
                0.0, 10.0),
            // enable / good signals
            Rail("ENABLE", "enable", 0.1, 20,
                new[] { (0.0, 0.0), (0.5, 0.0), (0.6, 1800.0), (9.0, 1800.0), (9.1, 0.0), (10.0, 0.0) },
                0.0, 10.0),
            Rail("PG_1V8", "good", 0.1, 15,
                new[] { (0.0, 0.0), (3.0, 0.0), (3.1, 1800.0), (7.6, 1800.0), (7.7, 0.0), (10.0, 0.0) },
                0.0, 10.0),
            // Raw capture in an unconvertible unit: kept in place with a diagnostic, never dropped for plotting.
            Rail("AUX_RAW", "rail", 0.2, 3,
                new[] { (0.0, 12.0), (5.0, 400.0), (10.0, 20.0) },
                0.0, 10.0, unit: "LSB"),
        };
        return WaveformSet.Create(parentId, parentId is null ? "初始导入：多轨压采样 + enable/good" : "局部修订：补齐 VDD_3V3 时钟缺口",
            "hw-verify", clock.Now(), rails);
    }

    public static ThresholdSet InitialThresholds(IClock clock, string? parentId = null, double vdd1v8MinStable = 0.5)
    {
        var specs = new[]
        {
            new ThresholdSpec("VDD_3V3", 2970, 2800, 0.5),
            new ThresholdSpec("VDD_1V8", 1620, 1530, vdd1v8MinStable),
            new ThresholdSpec("VDD_0V9", 810, 765, 0.3),
            new ThresholdSpec("VDD_1V2", 1080, 1020, 0.3),
            new ThresholdSpec("ENABLE", 1200, 1000, 0.2),
            new ThresholdSpec("PG_1V8", 1500, 1400, 0.2),
            new ThresholdSpec("AUX_RAW", 300, 250, 0.5),
        };
        return ThresholdSet.Create(parentId, parentId is null ? "时序约束 v1（版本化阈值）" : "阈值分叉：VDD_1V8 最小稳定时间 0.5→0.1 ms",
            "hw-verify", clock.Now(), specs);
    }

    public static DependencyGraph InitialGraph(IClock clock)
    {
        var edges = new[]
        {
            DependencyEdge.Create("VDD_3V3", "VDD_1V8", 0.5, 5.0),
            DependencyEdge.Create("VDD_1V8", "VDD_0V9", 0.3, 5.0),
            DependencyEdge.Create("VDD_1V8", "VDD_1V2", 0.3, 5.0),
            // Cyclic dependency demo: two simple cycles sharing edges -> multiple minimal contradiction sets.
            DependencyEdge.Create("AUX_A", "AUX_B", 0.1, 1.0),
            DependencyEdge.Create("AUX_B", "AUX_C", 0.1, 1.0),
            DependencyEdge.Create("AUX_C", "AUX_A", 0.1, 1.0),
            DependencyEdge.Create("AUX_C", "AUX_D", 0.1, 1.0),
            DependencyEdge.Create("AUX_D", "AUX_A", 0.1, 1.0),
        };
        return DependencyGraph.Create(null, "轨压依赖图 v1（含 AUX 循环依赖反例）", "hw-verify", clock.Now(), edges);
    }

    /// <summary>Replays the full case narrative and returns the key result ids for assertions.</summary>
    public static CasePackOutcome Apply(Workspace ws)
    {
        // 1. Initial import: raw waveforms, thresholds, hysteresis, min stable time, graph, clock, error preserved.
        var wv1 = InitialWaveforms(ws.Clock);
        var th1 = InitialThresholds(ws.Clock);
        var dg1 = InitialGraph(ws.Clock);
        ws.ImportWaveforms(wv1);
        ws.ImportThresholds(th1);
        ws.ImportGraph(dg1);

        // 2. Parameter revision: fork the threshold version, tightening only VDD_1V8 min stable time.
        //    Only results depending on VDD_1V8 expire (threshold-version partial staleness).
        var th2 = InitialThresholds(ws.Clock, parentId: th1.Id, vdd1v8MinStable: 0.1);
        ws.ImportThresholds(th2, forkNote: "评审要求：把 0.2 ms 下凹按违约处理");

        // 3. Local source change: fill the VDD_3V3 clock gap; only VDD_3V3-dependent results expire.
        var wv2 = InitialWaveforms(ws.Clock, parentId: wv1.Id, fillClockGap: true);
        ws.ImportWaveforms(wv2);

        // 4. Conflicting human decisions on the same glitch/violation judgment, then resolution + undo.
        var oneVEight = ws.Results()
            .Where(r => r.Result.Kind == "state-windows" && r.Result.Scope.Any(s => s.Rail == "VDD_1V8"))
            .OrderBy(r => r.Result.CreatedAt, StringComparer.Ordinal)
            .Last();
        var d1 = ws.Decide(oneVEight.Result.Id, "accept", "0.2 ms 下凹按 v1 阈值是 glitch，接受基线判定", "reviewer-a");
        var d2 = ws.Decide(oneVEight.Result.Id, "reject", "新阈值版本下同一区段构成违约，拒绝接受", "reviewer-b");
        ws.UndoDecision(d2.Id, "reviewer-b");
        var d3 = ws.Decide(oneVEight.Result.Id, "adjust", "保留两版阈值各自结论，以分叉版本为准继续评审", "reviewer-b");

        return new CasePackOutcome(wv1.Id, th1.Id, th2.Id, wv2.Id, dg1.Id, oneVEight.Result.Id, d1.Id, d2.Id, d3.Id);
    }
}

public sealed record CasePackOutcome(
    string WaveformV1, string ThresholdV1, string ThresholdV2, string WaveformV2,
    string GraphV1, string DecidedResultId, string Decision1, string Decision2, string Decision3);
