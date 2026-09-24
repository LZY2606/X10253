using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PowerSeq.Domain;

/// <summary>
/// 电源时序分析器。上升/下降使用不同阈值（滞回）；
/// 短暂越界按持续时间判定为 glitch 或违约；
/// 同时到达保留全部合法候选；时钟缺口产生 unknown。
/// 所有候选、拒绝原因、误差与未知状态都是一等数据。
/// </summary>
public static class Analyzer
{
    public const string AlgorithmVersion = "analyzer/1.0.0";
    public const double GapFactor = 1.5;

    public const string RuleHysteresis = "rule:hysteresis.dual-threshold";
    public const string RuleGlitch = "rule:glitch.duration<minStable";
    public const string RuleGoodLost = "rule:violation.good-lost.duration>=minStable";
    public const string RuleRiseAbort = "rule:glitch.rise-abort.duration<minStable";
    public const string RuleStartupOrder = "rule:violation.startup-order";
    public const string RuleMinGap = "rule:violation.min-gap";
    public const string RuleShutdownReverse = "rule:violation.shutdown-reverse-order";
    public const string RuleSimultaneous = "rule:unknown.simultaneous-arrival";
    public const string RuleClockGap = "rule:unknown.clock-gap";
    public const string RuleCycle = "rule:conflict.cyclic-dependency.min-conflict-set";

    public sealed record ClockGap(double T0Ms, double T1Ms);
    public sealed record StateWindow(string Rail, string State, double T0Ms, double T1Ms, string Certainty, string Note);
    public sealed record Excursion(string Id, string Rail, string Kind, string Rule, double T0Ms, double DurationMs, double ExtremumV, string Detail, string Status);

    public sealed record RailAnalysis(
        string Rail,
        IReadOnlyList<StateWindow> Windows,
        IReadOnlyList<Excursion> Excursions,
        IReadOnlyList<ClockGap> Gaps,
        double? GoodOnMs,
        double? GoodOffMs,
        IReadOnlyList<string> Diagnostics);

    public sealed record OrderJudgement(string Edge, string Status, string Rule, string Detail);
    public sealed record OrderCandidate(string Id, IReadOnlyList<string> Rails, string Status, string Reason);
    public sealed record CycleConflict(IReadOnlyList<IReadOnlyList<string>> Cycles, IReadOnlyList<IReadOnlyList<string>> MinimalConflictSets);
    public sealed record ViolationEvidence(string Id, string Kind, string Rule, double T0Ms, string Detail, IReadOnlyList<string> InvolvedRails);

    public sealed record GlobalAnalysis(
        IReadOnlyList<OrderJudgement> StartupJudgements,
        IReadOnlyList<OrderJudgement> ShutdownJudgements,
        IReadOnlyList<OrderCandidate> OrderCandidates,
        CycleConflict Cycles,
        ViolationEvidence? FirstViolation,
        IReadOnlyList<ViolationEvidence> AllViolations);

    public static string Fingerprint(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string ExcId(string rail, string kind, double t0Ms) =>
        $"exc-{rail}-{kind}-{((long)Math.Round(t0Ms * 1000)).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>单轨滞回状态机：off --(v≥rise 持续 minStable)--> good --(v≤fall 持续 minStable)--> off。</summary>
    public static RailAnalysis AnalyzeRail(RailSamples rail, RailThreshold th)
    {
        var rise = th.RiseV;
        var fall = th.FallV;
        var minStable = th.MinStableMs;
        var pts = rail.Samples;
        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();
        var gaps = new List<ClockGap>();
        var diags = new List<string>(rail.Diagnostics);

        for (var i = 1; i < pts.Count; i++)
        {
            var dt = pts[i].TMs - pts[i - 1].TMs;
            if (dt > GapFactor * rail.SamplePeriodMs)
                gaps.Add(new ClockGap(pts[i - 1].TMs, pts[i].TMs));
        }
        bool InGap(double t) => gaps.Any(g => t > g.T0Ms && t < g.T1Ms);
        string CertaintyOf(double t0, double t1) =>
            gaps.Any(g => g.T0Ms < t1 && g.T1Ms > t0) ? "unknown" : "confirmed";

        if (pts.Count == 0)
            return new RailAnalysis(rail.Name, windows, excursions, gaps, null, null, diags);

        double? goodOn = null, goodOff = null;
        var state = "off";
        var winStart = pts[0].TMs;
        var i = 0;
        while (i < pts.Count)
        {
            if (state == "off")
            {
                if (pts[i].V >= rise)
                {
                    // 确认期：允许回落到 [fall, rise) 滞回带，不允许跌破 fall
                    var k = i;
                    var aborted = false;
                    while (k < pts.Count && pts[k].TMs - pts[i].TMs < minStable)
                    {
                        if (pts[k].V < fall) { aborted = true; break; }
                        k++;
                    }
                    if (aborted)
                    {
                        var dur = pts[k].TMs - pts[i].TMs;
                        var ext = pts.Skip(i).Take(k - i + 1).Min(p => p.V);
                        var status = gaps.Any(g => g.T0Ms < pts[k].TMs && g.T1Ms > pts[i].TMs) ? "unknown" : "candidate";
                        excursions.Add(new Excursion(ExcId(rail.Name, "rise-abort", pts[i].TMs), rail.Name, "glitch",
                            RuleRiseAbort, pts[i].TMs, dur, ext,
                            $"上升越过 rise={rise}V 后 {dur:0.###}ms 内跌破 fall={fall}V（< minStable={minStable}ms），判为 glitch 而非有效上电", status));
                        i = k + 1;
                        continue;
                    }
                    // 确认成功
                    windows.Add(new StateWindow(rail.Name, "off", winStart, pts[i].TMs, CertaintyOf(winStart, pts[i].TMs), ""));
                    state = "good";
                    winStart = pts[i].TMs;
                    goodOn ??= pts[i].TMs;
                    i++;
                }
                else i++;
            }
            else // good
            {
                if (pts[i].V <= fall)
                {
                    // 找恢复点：重新越过 rise 才算滞回恢复
                    var k = i;
                    while (k < pts.Count && pts[k].V < rise) k++;
                    if (k >= pts.Count)
                    {
                        // 未恢复：正常关机下降沿
                        goodOff ??= pts[i].TMs;
                        windows.Add(new StateWindow(rail.Name, "good", winStart, pts[i].TMs, CertaintyOf(winStart, pts[i].TMs), ""));
                        state = "off";
                        winStart = pts[i].TMs;
                        i++;
                        continue;
                    }
                    var dur = pts[k].TMs - pts[i].TMs;
                    var ext = pts.Skip(i).Take(k - i).Min(p => p.V);
                    var spanGap = gaps.Any(g => g.T0Ms < pts[k].TMs && g.T1Ms > pts[i].TMs);
                    if (dur < minStable)
                    {
                        excursions.Add(new Excursion(ExcId(rail.Name, "droop", pts[i].TMs), rail.Name, "glitch",
                            RuleGlitch, pts[i].TMs, dur, ext,
                            $"跌破 fall={fall}V 持续 {dur:0.###}ms < minStable={minStable}ms，判为 glitch 而非违约", spanGap ? "unknown" : "candidate"));
                    }
                    else
                    {
                        excursions.Add(new Excursion(ExcId(rail.Name, "good-lost", pts[i].TMs), rail.Name, "violation",
                            RuleGoodLost, pts[i].TMs, dur, ext,
                            $"跌破 fall={fall}V 持续 {dur:0.###}ms ≥ minStable={minStable}ms，判为违约（power-good 丢失）", spanGap ? "unknown" : "candidate"));
                        windows.Add(new StateWindow(rail.Name, "good", winStart, pts[i].TMs, CertaintyOf(winStart, pts[i].TMs), ""));
                        state = "off";
                        winStart = pts[i].TMs;
                    }
                    i = k;
                }
                else i++;
            }
        }
        windows.Add(new StateWindow(rail.Name, state, winStart, pts[^1].TMs, CertaintyOf(winStart, pts[^1].TMs), ""));
        return new RailAnalysis(rail.Name, windows, excursions, gaps, goodOn, goodOff, diags);
    }

    /// <summary>全局：启动顺序、关机逆序、同时到达候选、循环依赖最小矛盾边集、第一违约证据。</summary>
    public static GlobalAnalysis AnalyzeGlobal(
        WaveformSet wf, ThresholdSet th, DepGraph deps,
        IReadOnlyDictionary<string, RailAnalysis> rails)
    {
        var startup = new List<OrderJudgement>();
        var shutdown = new List<OrderJudgement>();
        var candidates = new List<OrderCandidate>();
        var violations = new List<ViolationEvidence>();

        double TolOf(string a, string b)
        {
            var pa = wf.Rails.FirstOrDefault(r => r.Name == a)?.SamplePeriodMs ?? 0;
            var pb = wf.Rails.FirstOrDefault(r => r.Name == b)?.SamplePeriodMs ?? 0;
            return Math.Max(pa, pb);
        }

        foreach (var edge in deps.Edges)
        {
            var tol = TolOf(edge.Before, edge.After);
            var ra = rails.GetValueOrDefault(edge.Before);
            var rb = rails.GetValueOrDefault(edge.After);
            if (ra?.GoodOnMs == null || rb?.GoodOnMs == null)
            {
                startup.Add(new OrderJudgement(edge.Key, "unknown", RuleClockGap,
                    $"轨 {(ra?.GoodOnMs == null ? edge.Before : edge.After)} 未确认到达 good，顺序不可判定"));
                continue;
            }
            var dt = rb.GoodOnMs.Value - ra.GoodOnMs.Value;
            if (dt < -tol)
            {
                startup.Add(new OrderJudgement(edge.Key, "violation", RuleStartupOrder,
                    $"{edge.After} 在 {edge.Before} 之前 {(-dt):0.###}ms 到达 good（容差 ±{tol:0.###}ms）"));
                violations.Add(new ViolationEvidence($"vio-startup-{edge.Key}", "startup-order", RuleStartupOrder,
                    rb.GoodOnMs.Value, $"{edge.After} 先于 {edge.Before} 上电", new[] { edge.Before, edge.After }));
            }
            else if (Math.Abs(dt) <= tol)
            {
                startup.Add(new OrderJudgement(edge.Key, "unknown", RuleSimultaneous,
                    $"{edge.Before} 与 {edge.After} 在 ±{tol:0.###}ms 内同时到达，顺序不可唯一决定，保留全部候选"));
                candidates.Add(new OrderCandidate($"cand-order-{edge.Before}-before-{edge.After}",
                    new[] { edge.Before, edge.After }, "candidate", $"同时到达（Δ={dt:0.###}ms ≤ 采样时钟容差 {tol:0.###}ms）"));
                candidates.Add(new OrderCandidate($"cand-order-{edge.After}-before-{edge.Before}",
                    new[] { edge.After, edge.Before }, "candidate", $"同时到达（Δ={dt:0.###}ms ≤ 采样时钟容差 {tol:0.###}ms）"));
            }
            else if (edge.MinGap != null)
            {
                var minGapMs = edge.MinGap.ToCanonical(Quantity.TimeCanonical, out var diag);
                if (minGapMs == null)
                    startup.Add(new OrderJudgement(edge.Key, "unknown", RuleClockGap, diag!));
                else if (dt < minGapMs.Value)
                {
                    startup.Add(new OrderJudgement(edge.Key, "violation", RuleMinGap,
                        $"{edge.Key} 间隔 {dt:0.###}ms < minGap {minGapMs.Value:0.###}ms"));
                    violations.Add(new ViolationEvidence($"vio-mingap-{edge.Key}", "min-gap", RuleMinGap,
                        ra.GoodOnMs.Value, $"{edge.Key} 间隔不足", new[] { edge.Before, edge.After }));
                }
                else startup.Add(new OrderJudgement(edge.Key, "ok", "", $"间隔 {dt:0.###}ms ≥ minGap {minGapMs.Value:0.###}ms"));
            }
            else startup.Add(new OrderJudgement(edge.Key, "ok", "", $"顺序正确，Δ={dt:0.###}ms"));

            // 关机逆序：A before B 意味着 B 必须先于 A 掉电
            if (ra.GoodOffMs != null && rb.GoodOffMs != null)
            {
                var dOff = ra.GoodOffMs.Value - rb.GoodOffMs.Value; // B 先掉电 → dOff > 0
                if (dOff < -tol)
                {
                    shutdown.Add(new OrderJudgement(edge.Key, "violation", RuleShutdownReverse,
                        $"关机逆序违约：{edge.Before} 先于 {edge.After} 掉电 {(-dOff):0.###}ms（要求 {edge.After} 先掉电）"));
                    violations.Add(new ViolationEvidence($"vio-shutdown-{edge.Key}", "shutdown-reverse", RuleShutdownReverse,
                        ra.GoodOffMs.Value, $"{edge.Before} 先于 {edge.After} 掉电", new[] { edge.Before, edge.After }));
                }
                else if (Math.Abs(dOff) <= tol)
                    shutdown.Add(new OrderJudgement(edge.Key, "unknown", RuleSimultaneous,
                        $"关机同时到达（Δ={dOff:0.###}ms ≤ {tol:0.###}ms），无法唯一判定"));
                else
                    shutdown.Add(new OrderJudgement(edge.Key, "ok", "", $"关机逆序正确，Δ={dOff:0.###}ms"));
            }
        }

        // 轨内违约（good-lost）并入证据
        foreach (var ra in rails.Values)
            foreach (var ex in ra.Excursions.Where(e => e.Kind == "violation"))
                violations.Add(new ViolationEvidence(ex.Id, "good-lost", ex.Rule, ex.T0Ms, ex.Detail, new[] { ex.Rail }));

        var cycles = CycleFinder.Analyze(deps.Edges);
        var first = violations.OrderBy(v => v.T0Ms).ThenBy(v => v.Id).FirstOrDefault();
        return new GlobalAnalysis(startup, shutdown, candidates, cycles, first, violations);
    }
}
