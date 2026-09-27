using PowerEvidence.Domain;

namespace PowerEvidence.Engine;

public sealed record RailEvent(string Kind, double T, double? TEnd, string Detail, bool Indeterminate);
public sealed record StateWindow(string State, double Start, double End);
public sealed record Candidate(int Index, string Label, string Reason);
public sealed record Ambiguity(string Id, string Kind, string Rail, string Detail, IReadOnlyList<Candidate> Candidates);

public sealed record RailAnalysis(
    string Rail,
    IReadOnlyList<RailEvent> Events,
    IReadOnlyList<StateWindow> HighWindows,
    IReadOnlyList<StateWindow> UnknownWindows,
    IReadOnlyList<Ambiguity> Ambiguities,
    IReadOnlyList<string> Diagnostics)
{
    public double? FirstRise => Events.FirstOrDefault(e => e.Kind == "rise")?.T;
    public double? FirstFall => Events.FirstOrDefault(e => e.Kind == "fall")?.T;
}

/// <summary>
/// 轨压状态机（算法版本 rail-window-v1）：
/// 上升用上升阈值、下降用下降阈值（滞回）；候选持续不足 minStable 判为 glitch，
/// 持续达到 minStable 判为真实跳变；测量误差重叠区产生不确定状态与候选。
/// </summary>
public static class RailAnalyzer
{
    public const string AlgoVersion = "rail-window-v1";

    public static RailAnalysis Analyze(RailWaveform waveform, RailThreshold th, ClockSpec clock)
    {
        var events = new List<RailEvent>();
        var highWindows = new List<StateWindow>();
        var unknownWindows = new List<StateWindow>();
        var ambiguities = new List<Ambiguity>();
        var diagnostics = new List<string>();
        var rail = waveform.Rail;

        var samples = waveform.Samples.Where(s => !double.IsNaN(s.T)).OrderBy(s => s.T).ToList();
        var dropped = waveform.Samples.Count - samples.Count;
        if (dropped > 0)
            diagnostics.Add($"诊断: {dropped} 个样本时间无法转换，保留在原始输入中但不参与计算");

        if (double.IsNaN(th.Rise.Value) || double.IsNaN(th.Fall.Value) || double.IsNaN(th.MinStable))
        {
            diagnostics.Add($"诊断: rail {rail} 阈值或最小稳定时间无法规范化，状态未知");
            return new RailAnalysis(rail, events, highWindows,
                new[] { new StateWindow("unknown", samples.FirstOrDefault()?.T ?? 0, samples.LastOrDefault()?.T ?? 0) },
                ambiguities, diagnostics);
        }

        var rise = th.Rise.Value;
        var fall = th.Fall.Value;
        var hyst = th.Hysteresis;
        var minStable = th.MinStable;
        var gapLimit = double.IsNaN(clock.Period) ? double.PositiveInfinity : clock.Period * 1.5 + clock.Error;
        if (double.IsNaN(clock.Period))
            diagnostics.Add($"诊断: 采样时钟单位未知，时钟缺口检测停用");

        var high = false;
        double? candidateStart = null;
        double? lastBeyondT = null;
        double? highSince = null;
        var candidateIndeterminate = false;
        Sample? prev = null;

        foreach (var s in samples)
        {
            if (prev is not null && s.T - prev.T > gapLimit)
            {
                events.Add(new RailEvent("clock-gap", prev.T, s.T,
                    $"采样缺口 {(s.T - prev.T):0.###}ms 超过时钟周期 {clock.Period:0.###}{clock.Unit} 的 1.5 倍", false));
                unknownWindows.Add(new StateWindow("unknown", prev.T, s.T));
                if (candidateStart is not null)
                {
                    diagnostics.Add($"诊断: 确认窗口跨越时钟缺口 [{prev.T},{s.T}]，候选作废，状态记为未知");
                    candidateStart = null;
                    candidateIndeterminate = false;
                }
            }

            if (!high)
            {
                var above = s.Voltage.DefinitelyAbove(rise);
                if (above == true)
                {
                    candidateStart ??= s.T;
                    lastBeyondT = s.T;
                    if (s.T - candidateStart >= minStable)
                    {
                        events.Add(new RailEvent("rise", candidateStart.Value, null,
                            $"上升确认: 高于上升阈值 {rise}V 持续 {s.T - candidateStart:0.###}ms ≥ minStable {minStable}ms",
                            candidateIndeterminate));
                        high = true;
                        highSince = candidateStart;
                        candidateStart = null;
                        candidateIndeterminate = false;
                    }
                }
                else if (above == false && s.Voltage.DefinitelyAbove(rise - hyst) == false)
                {
                    if (candidateStart is not null && lastBeyondT is not null)
                    {
                        var duration = lastBeyondT.Value - candidateStart.Value;
                        events.Add(new RailEvent("glitch-high", candidateStart.Value, lastBeyondT.Value,
                            $"glitch 判定: 高于上升阈值 {rise}V 仅持续 {duration:0.###}ms < minStable {minStable}ms，按滞回 {hyst}V 回落",
                            candidateIndeterminate));
                    }
                    candidateStart = null;
                    candidateIndeterminate = false;
                }
                else if (above is null && candidateStart is not null && !candidateIndeterminate)
                {
                    candidateIndeterminate = true;
                    var ambId = IdGen.ContentId("amb", $"{rail}|rise-confirm|{candidateStart}|{s.T}");
                    ambiguities.Add(new Ambiguity(ambId, "indeterminate-rise", rail,
                        $"t={s.T} 处样本 {s.Voltage.Raw}{s.Voltage.Unit}±{s.Voltage.Error} 与上升阈值 {rise}V 在误差带内重叠",
                        new Candidate[]
                        {
                            new(0, "rise-confirmed", "假设样本实际高于阈值，上升在候选起点确认"),
                            new(1, "glitch", "假设样本实际低于阈值，候选按 glitch 处理")
                        }));
                }
            }
            else
            {
                var aboveFall = s.Voltage.DefinitelyAbove(fall);
                if (aboveFall == false)
                {
                    candidateStart ??= s.T;
                    lastBeyondT = s.T;
                    if (s.T - candidateStart >= minStable)
                    {
                        events.Add(new RailEvent("fall", candidateStart.Value, null,
                            $"下降确认: 低于下降阈值 {fall}V 持续 {s.T - candidateStart:0.###}ms ≥ minStable {minStable}ms",
                            candidateIndeterminate));
                        high = false;
                        if (highSince is not null) highWindows.Add(new StateWindow("high", highSince.Value, candidateStart.Value));
                        highSince = null;
                        candidateStart = null;
                        candidateIndeterminate = false;
                    }
                }
                else if (s.Voltage.DefinitelyAbove(fall + hyst) == true)
                {
                    if (candidateStart is not null && lastBeyondT is not null)
                    {
                        var duration = lastBeyondT.Value - candidateStart.Value;
                        events.Add(new RailEvent("glitch-low", candidateStart.Value, lastBeyondT.Value,
                            $"glitch 判定: 低于下降阈值 {fall}V 仅持续 {duration:0.###}ms < minStable {minStable}ms，按滞回 {hyst}V 恢复",
                            candidateIndeterminate));
                    }
                    candidateStart = null;
                    candidateIndeterminate = false;
                }
                else if (candidateStart is not null)
                {
                    candidateIndeterminate = true;
                }
            }
            prev = s;
        }

        if (high && highSince is not null && samples.Count > 0)
            highWindows.Add(new StateWindow("high", highSince.Value, samples[^1].T));
        if (candidateStart is not null)
            diagnostics.Add($"诊断: 波形结束时仍有未确认候选（起点 t={candidateStart}），状态未知");

        return new RailAnalysis(rail, events, highWindows, unknownWindows, ambiguities, diagnostics);
    }
}
