namespace PowerSeq.Core;

public sealed record StateWindow(double StartNs, double EndNs, RailState State);

/// <summary>时间轴事件：glitch / gap / transition，全部机器可读。</summary>
public sealed record TimelineEvent(string Kind, string Rail, double AtNs, double DurationNs, string Detail);

/// <summary>判断依据：机器可读，说明某结论由哪条规则、哪些参数得出。</summary>
public sealed record Judgment(
    string Rule,
    string Subject,
    string Verdict,
    IReadOnlyDictionary<string, string> Data);

public sealed record RailTimeline(
    string Rail,
    IReadOnlyList<StateWindow> Windows,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<Judgment> Judgments)
{
    public double? FirstGoodNs => Windows.FirstOrDefault(w => w.State == RailState.Good)?.StartNs;

    /// <summary>首次从 Good 跌出的时刻（关机分析用）。</summary>
    public double? FirstFallNs
    {
        get
        {
            for (var i = 0; i < Windows.Count; i++)
                if (Windows[i].State == RailState.Bad && i > 0 && Windows.Take(i).Any(w => w.State == RailState.Good))
                    return Windows[i].StartNs;
            return null;
        }
    }
}

/// <summary>
/// 滞回状态机：上升用 RiseThresholdV、下降用 FallThresholdV（双阈值）；
/// 越过阈值的短暂停留若持续时间短于 MinStableNs 记为 glitch，否则记为真实跳变；
/// 采样间隔超过 NominalPeriodNs * GapFactor 记为时钟缺口，缺口区间状态为 Unknown。
/// </summary>
public static class StateMachine
{
    public const string RuleDualThreshold = "hysteresis.dual-threshold";
    public const string RuleGlitchVsViolation = "glitch-vs-violation.duration";
    public const string RuleClockGap = "clock.gap";

    public static RailTimeline Evaluate(
        string rail,
        IReadOnlyList<Sample> samples,
        RailThreshold threshold,
        ClockSpec clock)
    {
        threshold.Validate();
        var windows = new List<StateWindow>();
        var events = new List<TimelineEvent>();
        var judgments = new List<Judgment>
        {
            new(RuleDualThreshold, rail, "configured", new Dictionary<string, string>
            {
                ["riseThresholdV"] = threshold.RiseThresholdV.ToString("R"),
                ["fallThresholdV"] = threshold.FallThresholdV.ToString("R"),
                ["minStableNs"] = threshold.MinStableNs.ToString("R")
            })
        };
        if (samples.Count == 0)
            return new RailTimeline(rail, windows, events, judgments);

        var gapThreshold = clock.NominalPeriodNs * clock.GapFactor;

        RailState Classify(double v) =>
            v >= threshold.RiseThresholdV ? RailState.Good :
            v <= threshold.FallThresholdV ? RailState.Bad : RailState.Unknown;

        var state = Classify(samples[0].Volts);
        var windowStart = samples[0].TimeNs;
        (RailState State, double StartNs, double Peak)? pending = null;

        void CloseWindow(double endNs) => windows.Add(new StateWindow(windowStart, endNs, state));

        for (var i = 1; i < samples.Count; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
            var dt = cur.TimeNs - prev.TimeNs;
            if (dt > gapThreshold)
            {
                CloseWindow(prev.TimeNs);
                events.Add(new TimelineEvent("gap", rail, prev.TimeNs, dt,
                    $"clock gap {dt:0.##}ns > {gapThreshold:0.##}ns"));
                judgments.Add(new Judgment(RuleClockGap, rail, "gap", new Dictionary<string, string>
                {
                    ["startNs"] = prev.TimeNs.ToString("R"), ["endNs"] = cur.TimeNs.ToString("R"),
                    ["durationNs"] = dt.ToString("R"), ["gapThresholdNs"] = gapThreshold.ToString("R")
                }));
                windows.Add(new StateWindow(prev.TimeNs, cur.TimeNs, RailState.Unknown));
                windowStart = cur.TimeNs;
                pending = null;
            }

            var desired = state switch
            {
                RailState.Good => cur.Volts <= threshold.FallThresholdV ? RailState.Bad : RailState.Good,
                RailState.Bad => cur.Volts >= threshold.RiseThresholdV ? RailState.Good : RailState.Bad,
                _ => Classify(cur.Volts)
            };

            if (desired != state)
            {
                if (pending is null || pending.Value.State != desired)
                    pending = (desired, cur.TimeNs, cur.Volts);
                else
                    pending = (desired, pending.Value.StartNs,
                        desired == RailState.Good ? Math.Max(pending.Value.Peak, cur.Volts)
                                                  : Math.Min(pending.Value.Peak, cur.Volts));

                if (cur.TimeNs - pending.Value.StartNs >= threshold.MinStableNs)
                {
                    // 持续足够久：真实跳变，窗口边界取越过阈值的时刻。
                    CloseWindow(pending.Value.StartNs);
                    events.Add(new TimelineEvent("transition", rail, pending.Value.StartNs, 0,
                        $"{state}->{desired}"));
                    judgments.Add(new Judgment(RuleGlitchVsViolation, rail, "transition", new Dictionary<string, string>
                    {
                        ["from"] = state.ToString(), ["to"] = desired.ToString(),
                        ["atNs"] = pending.Value.StartNs.ToString("R"),
                        ["durationNs"] = (cur.TimeNs - pending.Value.StartNs).ToString("R"),
                        ["minStableNs"] = threshold.MinStableNs.ToString("R")
                    }));
                    state = desired;
                    windowStart = pending.Value.StartNs;
                    pending = null;
                }
            }
            else if (pending is not null)
            {
                // 未到最短稳定时间就回到阈内：glitch，不算违约。
                var duration = cur.TimeNs - pending.Value.StartNs;
                events.Add(new TimelineEvent("glitch", rail, pending.Value.StartNs, duration,
                    $"{pending.Value.State} excursion {duration:0.##}ns < minStable {threshold.MinStableNs:0.##}ns, peak {pending.Value.Peak:0.###}V"));
                judgments.Add(new Judgment(RuleGlitchVsViolation, rail, "glitch", new Dictionary<string, string>
                {
                    ["towards"] = pending.Value.State.ToString(),
                    ["atNs"] = pending.Value.StartNs.ToString("R"),
                    ["durationNs"] = duration.ToString("R"),
                    ["minStableNs"] = threshold.MinStableNs.ToString("R"),
                    ["peakV"] = pending.Value.Peak.ToString("R")
                }));
                pending = null;
            }
        }

        var last = samples[^1];
        if (pending is not null)
        {
            var duration = last.TimeNs - pending.Value.StartNs;
            if (duration >= threshold.MinStableNs)
            {
                CloseWindow(pending.Value.StartNs);
                events.Add(new TimelineEvent("transition", rail, pending.Value.StartNs, 0, $"{state}->{pending.Value.State}"));
                state = pending.Value.State;
                windowStart = pending.Value.StartNs;
            }
            else
            {
                events.Add(new TimelineEvent("glitch", rail, pending.Value.StartNs, duration,
                    $"truncated {pending.Value.State} excursion {duration:0.##}ns at end of capture"));
            }
        }
        CloseWindow(last.TimeNs);
        return new RailTimeline(rail, windows, events, judgments);
    }
}
