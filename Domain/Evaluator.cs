using System.Text.Json.Nodes;

namespace PowerSequencer.Domain;

public enum WindowState { Low, High, Unknown }

public sealed record StateWindow(WindowState State, double StartMs, double EndMs)
{
    public double DurationMs => EndMs - StartMs;
}

public enum ExcursionKind { Glitch, Violation }

public sealed record Excursion(
    string Rail, string Direction, double StartMs, double EndMs,
    double DurationMs, double ExtremumV, ExcursionKind Kind, string Rule);

public sealed record RailEval(
    string Rail, double RiseThresholdV, double FallThresholdV, double HysteresisV, double MinStableTimeMs,
    IReadOnlyList<StateWindow> Windows, IReadOnlyList<Excursion> Excursions,
    IReadOnlyList<ClockGap> Gaps, double? FirstGoodMs, double? LastGoodMs, bool Evaluable);

public sealed record ClockGap(double StartMs, double EndMs, bool HidesTransition);

public sealed record CandidateRecord(string Id, string Label, JsonNode Detail, string Status, string? Reason);

public sealed record FindingRecord(
    string Id, string Kind, string Severity, IReadOnlyList<string> Rails,
    string Summary, JsonNode Evidence, IReadOnlyList<CandidateRecord> Candidates);

public sealed record EvaluationResult(
    string AlgoVersion,
    IReadOnlyDictionary<string, RailEval> Rails,
    IReadOnlyList<FindingRecord> Findings)
{
    public IReadOnlyList<FindingRecord> Violations => Findings
        .Where(f => f.Severity == "error" || f.Kind == "cyclic-dependency").ToList();
}

public static class Evaluator
{
    public const string AlgoVersion = "seq-eval/1.0";

    public static EvaluationResult Evaluate(WaveformBundle bundle, ThresholdSet thresholds)
    {
        var findings = new List<FindingRecord>();

        if (!Units.TryToMs(bundle.SampleClockPeriod, bundle.TimeUnit, out var periodMs))
            findings.Add(Diag("clock-unit", new[] { "*" },
                $"采样时钟单位 {bundle.TimeUnit} 无法转换为 ms，保留原值",
                Node(("rawPeriod", bundle.SampleClockPeriod), ("rawUnit", bundle.TimeUnit))));
        var errorConvertible = Units.TryToVolts(bundle.MeasurementError, bundle.ErrorUnit, out var errorV);

        var rails = new Dictionary<string, RailEval>();
        foreach (var rail in bundle.Rails)
        {
            if (!thresholds.Rails.TryGetValue(rail.Name, out var th))
            {
                findings.Add(Diag("missing-threshold", new[] { rail.Name },
                    $"轨 {rail.Name} 没有阈值定义，保留原始波形不参与判定",
                    Node(("rail", rail.Name))));
                continue;
            }

            JsonNode? unitDiag = null;
            if (!Units.TryToVolts(1, rail.Unit, out _))
                unitDiag = Node(("rail", rail.Name), ("rawUnit", rail.Unit),
                    ("reason", "电压单位无法转换，原始值保留在原位置"));
            if (!Units.TryToMs(1, bundle.TimeUnit, out _))
                unitDiag = Node(("rail", rail.Name), ("rawTimeUnit", bundle.TimeUnit),
                    ("reason", "时间单位无法转换，原始值保留在原位置"));
            if (unitDiag is not null)
            {
                findings.Add(Diag("unit-diagnostic", new[] { rail.Name },
                    $"轨 {rail.Name} 单位无法规范化，仅保留原始表示", unitDiag));
                rails[rail.Name] = new RailEval(rail.Name, 0, 0, 0, 0,
                    Array.Empty<StateWindow>(), Array.Empty<Excursion>(),
                    Array.Empty<ClockGap>(), null, null, false);
                continue;
            }

            var riseV = Units.TryToVolts(th.RiseThreshold, th.Unit, out var rv) ? rv : 0;
            var fallV = Units.TryToVolts(th.FallThreshold, th.Unit, out var fv) ? fv : 0;
            var hystV = riseV - fallV;
            var stableMs = Units.TryToMs(th.MinStableTime, th.TimeUnit, out var sm) ? sm : 0;

            var samples = rail.Samples
                .Select(p => (t: Units.TryToMs(p.Time, bundle.TimeUnit, out var tm) ? tm : p.Time,
                              v: Units.TryToVolts(p.Value, rail.Unit, out var vm) ? vm : p.Value))
                .ToList();
            rails[rail.Name] = EvaluateRail(rail.Name, samples, riseV, fallV, hystV, stableMs, periodMs, findings);
        }

        EvaluateDependencies(thresholds, rails, periodMs, findings);
        findings.AddRange(CycleAnalysis(thresholds));
        findings.AddRange(WindowFindings(rails, periodMs, stableFor: thresholds));

        return new EvaluationResult(AlgoVersion, rails, findings.OrderBy(f => f.Kind).ThenBy(f => f.Id).ToList());
    }

    static RailEval EvaluateRail(string name, List<(double t, double v)> s,
        double riseV, double fallV, double hystV, double stableMs, double periodMs, List<FindingRecord> findings)
    {
        var gaps = new List<ClockGap>();
        for (var i = 1; i < s.Count; i++)
        {
            var dt = s[i].t - s[i - 1].t;
            if (periodMs > 0 && dt > 1.5 * periodMs)
            {
                var hides = RawState(s[i - 1].v, riseV, fallV) != RawState(s[i].v, riseV, fallV);
                gaps.Add(new ClockGap(s[i - 1].t, s[i].t, hides));
                if (hides)
                {
                    var ev = Node(("rail", name), ("gapStartMs", s[i - 1].t), ("gapEndMs", s[i].t),
                        ("gapDurationMs", dt), ("clockPeriodMs", periodMs),
                        ("rule", "缺口两侧状态不同 => 穿越时刻不可定位，结论为未知"),
                        ("algoVersion", AlgoVersion));
                    findings.Add(MakeFinding("clock-gap", "unknown", new[] { name },
                        $"轨 {name} 在 {s[i - 1].t}–{s[i].t}ms 存在时钟缺口且穿越落在缺口内", ev,
                        MakeCandidates(("unknown", "未知: 无法唯一决定",
                            Node(("possibleMs", s[i - 1].t), ("possibleMs2", s[i].t))))));
                }
                else
                {
                    var ev = Node(("rail", name), ("gapStartMs", s[i - 1].t), ("gapEndMs", s[i].t),
                        ("gapDurationMs", dt), ("clockPeriodMs", periodMs),
                        ("rule", "缺口两侧状态一致 => 区间标记未知但不产生违约"));
                    findings.Add(MakeFinding("clock-gap", "info", new[] { name },
                        $"轨 {name} 在 {s[i - 1].t}–{s[i].t}ms 存在时钟缺口", ev,
                        MakeCandidates(("observed", "观测: 缺口两侧状态一致",
                            Node(("state", RawState(s[i - 1].v, riseV, fallV).ToString()))))));
                }
            }
        }

        var raw = s.Select(p => RawState(p.v, riseV, fallV)).ToList();
        var runs = BuildRuns(raw, s);
        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();
        foreach (var (run, index) in runs.Select((r, i) => (r, i)))
        {
            var interior = index > 0 && index < runs.Count - 1;
            if (run.State == WindowState.Low && interior && windows.Count > 0)
            {
                // Enclosed dip while rail was high.
                var dur = run.EndMs - run.StartMs;
                var minV = s.Skip(run.StartIndex).Take(run.EndIndex - run.StartIndex + 1).Min(p => p.v);
                if (dur < stableMs)
                {
                    excursions.Add(new Excursion(name, "dip", run.StartMs, run.EndMs, dur, minV,
                        ExcursionKind.Glitch, $"duration({dur}ms) < minStableTime({stableMs}ms) => glitch"));
                    var merged = windows[^1];
                    windows[^1] = merged with { EndMs = run.EndMs };
                    if (index + 1 < runs.Count) windows[^1] = windows[^1] with { EndMs = runs[index + 1].EndMs };
                }
                else
                {
                    excursions.Add(new Excursion(name, "dip", run.StartMs, run.EndMs, dur, minV,
                        ExcursionKind.Violation, $"duration({dur}ms) >= minStableTime({stableMs}ms) => dropout violation"));
                    windows.Add(new StateWindow(WindowState.Low, run.StartMs, run.EndMs));
                }
            }
            else if (run.State == WindowState.High && interior)
            {
                var dur = run.EndMs - run.StartMs;
                var maxV = s.Skip(run.StartIndex).Take(run.EndIndex - run.StartIndex + 1).Max(p => p.v);
                if (dur < stableMs)
                {
                    excursions.Add(new Excursion(name, "spike", run.StartMs, run.EndMs, dur, maxV,
                        ExcursionKind.Glitch, $"duration({dur}ms) < minStableTime({stableMs}ms) => glitch"));
                    windows.Add(new StateWindow(WindowState.Low, run.StartMs, run.EndMs));
                }
                else windows.Add(new StateWindow(WindowState.High, run.StartMs, run.EndMs));
            }
            else windows.Add(new StateWindow(run.State, run.StartMs, run.EndMs));
        }
        foreach (var g in gaps) windows.Add(new StateWindow(WindowState.Unknown, g.StartMs, g.EndMs));

        var highs = windows.Where(w => w.State == WindowState.High).OrderBy(w => w.StartMs).ToList();
        var eval = new RailEval(name, riseV, fallV, hystV, stableMs,
            windows.OrderBy(w => w.StartMs).ToList(), excursions, gaps,
            highs.FirstOrDefault()?.StartMs, highs.LastOrDefault()?.EndMs, true);

        foreach (var ex in excursions)
        {
            var kind = ex.Kind == ExcursionKind.Glitch ? "glitch" : "dropout-violation";
            var sev = ex.Kind == ExcursionKind.Glitch ? "warning" : "error";
            var ev = Node(("rail", name), ("direction", ex.Direction),
                ("startMs", ex.StartMs), ("endMs", ex.EndMs), ("durationMs", ex.DurationMs),
                ("extremumV", ex.ExtremumV), ("minStableTimeMs", stableMs),
                ("riseThresholdV", riseV), ("fallThresholdV", fallV), ("hysteresisV", hystV),
                ("classification", ex.Kind.ToString().ToLowerInvariant()),
                ("rule", "短暂越界按持续时间判定: < minStableTime 为 glitch，>= minStableTime 为违约"),
                ("basisRule", ex.Rule), ("algoVersion", AlgoVersion),
                ("units", Node(("time", "ms"), ("voltage", "V"))));
            findings.Add(MakeFinding(kind, sev, new[] { name },
                $"{name} {ex.Direction} {ex.DurationMs}ms => {ex.Kind} (极值 {ex.ExtremumV:0.###}V)",
                ev, MakeCandidates((ex.Kind.ToString().ToLowerInvariant(), ex.Kind == ExcursionKind.Glitch ? "接受: 判定为 glitch" : "接受: 判定为违约",
                    Node(("kind", ex.Kind.ToString()))))));
        }
        return eval;
    }

    static WindowState RawState(double v, double rise, double fall) =>
        v >= rise ? WindowState.High : v <= fall ? WindowState.Low : WindowState.Low;
    // NOTE: between fall and rise keeps previous state; initial state Low. RawState only labels
    // samples outside the hysteresis band; the run builder carries previous state through the band.

    sealed record Run(WindowState State, int StartIndex, int EndIndex, double StartMs, double EndMs);

    static List<Run> BuildRuns(List<WindowState> labels, List<(double t, double v)> s)
    {
        var runs = new List<Run>();
        var cur = WindowState.Low;
        var start = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var next = labels[i] switch
            {
                WindowState.High => WindowState.High,
                WindowState.Low => cur == WindowState.High && s[i].v > _Fall(labels) ? WindowState.High : WindowState.Low,
                _ => cur
            };
            // In-band samples (fall < v < rise): labels[i] is Low by RawState's default; hold cur.
            if (labels[i] == WindowState.Low && cur == WindowState.High && s[i].v > floatTreshold(runStart: start))
                next = WindowState.High;
            if (i > 0 && next != cur)
            {
                runs.Add(new Run(cur, start, i - 1, s[start].t, s[i - 1].t));
                start = i;
            }
            cur = next;
        }
        runs.Add(new Run(cur, start, s.Count - 1, s[start].t, s[^1].t));
        return runs;
    }

    static double floatTreshold(int runStart) => 0;
    static WindowState _Fall(List<WindowState> labels) => WindowState.Low;
