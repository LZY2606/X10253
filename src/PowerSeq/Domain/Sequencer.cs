namespace PowerSeq.Domain;

public record StateWindow(string Rail, string State, double StartMs, double EndMs, bool Uncertain);

public record Excursion(
    string Rail,
    string Direction,        // rise | fall
    double StartMs,
    double EndMs,
    double DurationMs,
    string Classification,   // glitch | violation
    string Rule);            // machine-readable judgment basis

public record RailAnalysis(
    string Rail,
    IReadOnlyList<StateWindow> Windows,
    IReadOnlyList<Excursion> Excursions,
    double? GoodAtMs,
    double? OffAtMs,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Per-rail power state machine.
/// Semantics (machine-readable rules emitted with each judgment):
///  - rise/fall use DIFFERENT thresholds: GOOD entry confirmed at rise_V + hyst_V,
///    real shutdown at fall_V - hyst_V (hysteresis dead zone suppresses chatter).
///  - A brief excursion past a threshold is a GLITCH when its duration is strictly
///    less than min_stable_ms, otherwise a VIOLATION.
///  - Sampling clock gaps (interval > 2x nominal period) open an 'unknown' window;
///    transitions interpolated across a gap are flagged uncertain.
/// </summary>
public static class Sequencer
{
    public const string AlgorithmVersion = "seq-1.1";

    public static RailAnalysis Analyze(RailWaveform wf, RailThreshold th)
    {
        var diags = wf.Diagnostics.ToList();
        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();
        var pts = wf.Normalized;
        if (pts.Count == 0)
        {
            diags.Add(new Diagnostic("waveform.empty", "没有可用的规范化采样点", null));
            return new RailAnalysis(wf.Rail, windows, excursions, null, null, diags);
        }

        var goodEntry = th.RiseV + th.HystV;   // confirmed-good level
        var offExit = th.FallV - th.HystV;     // confirmed-off level
        var state = pts[0].V >= goodEntry ? "good" : "off";
        var winStart = pts[0].TMs;
        double? goodAt = state == "good" ? pts[0].TMs : null;
        double? offAt = null;
        var gapAfter = new bool[pts.Count]; // gap between i-1 and i

        for (var i = 1; i < pts.Count; i++)
        {
            if (wf.ClockPeriodMs is { } cp && pts[i].TMs - pts[i - 1].TMs > 2.0 * cp)
            {
                gapAfter[i] = true;
                diags.Add(new Diagnostic("clock.gap",
                    $"采样时钟缺口: {pts[i - 1].TMs} ms 到 {pts[i].TMs} ms (间隔 {pts[i].TMs - pts[i - 1].TMs:F3} ms > 2x{cp} ms)", null));
            }
        }

        void CloseWindow(double end, bool uncertain)
        {
            if (end > winStart || windows.Count == 0)
                windows.Add(new StateWindow(wf.Rail, state, winStart, Math.Max(end, winStart), uncertain));
        }

        Excursion? open = null; // currently open excursion
        double openLevel = 0;

        for (var i = 1; i < pts.Count; i++)
        {
            var (t0, v0) = (pts[i - 1].TMs, pts[i - 1].V);
            var (t1, v1) = (pts[i].TMs, pts[i].V);
            var inGap = gapAfter[i];

            double? Cross(double level)
            {
                if ((v0 - level) * (v1 - level) > 0 || v0 == v1) return null;
                return t0 + (level - v0) * (t1 - t0) / (v1 - v0);
            }

            if (state == "off")
            {
                if (open is null)
                {
                    var up = Cross(th.RiseV);
                    if (up.HasValue && v1 > v0)
                    {
                        open = new Excursion(wf.Rail, "rise", up.Value, 0, 0, "", "");
                        openLevel = th.RiseV;
                    }
                }
                if (open is not null)
                {
                    // confirmed GOOD if we cross rise+hyst upward before falling back below rise
                    var confirm = Cross(goodEntry);
                    var back = Cross(th.RiseV);
                    if (confirm.HasValue && v1 > v0 && (!back.HasValue || confirm.Value <= back.Value))
                    {
                        if (inGap) diags.Add(new Diagnostic("clock.gap.transition", "good 进入时刻跨越时钟缺口，标记为不确定", null));
                        CloseWindow(confirm.Value, inGap);
                        state = "good";
                        winStart = confirm.Value;
                        goodAt ??= confirm.Value;
                        open = null;
                    }
                    else if (back.HasValue && v1 < v0)
                    {
                        var dur = back.Value - open.StartMs;
                        var cls = dur < th.MinStableMs ? "glitch" : "violation";
                        excursions.Add(open with
                        {
                            EndMs = back.Value,
                            DurationMs = dur,
                            Classification = cls,
                            Rule = $"rise-excursion duration {dur:F3} ms {(dur < th.MinStableMs ? "<" : ">=")} min_stable {th.MinStableMs} ms => {cls} (rise threshold {th.RiseV} V, hysteresis +{th.HystV} V)"
                        });
                        open = null;
                    }
                }
            }
            else // good
            {
                if (open is null)
                {
                    var down = Cross(th.FallV);
                    if (down.HasValue && v1 < v0)
                    {
                        open = new Excursion(wf.Rail, "fall", down.Value, 0, 0, "", "");
                        openLevel = th.FallV;
                    }
                }
                if (open is not null)
                {
                    var confirmOff = Cross(offExit);
                    var back = Cross(th.FallV);
                    if (confirmOff.HasValue && v1 < v0 && (!back.HasValue || confirmOff.Value <= back.Value))
                    {
                        if (inGap) diags.Add(new Diagnostic("clock.gap.transition", "off 退出时刻跨越时钟缺口，标记为不确定", null));
                        CloseWindow(confirmOff.Value, inGap);
                        state = "off";
                        winStart = confirmOff.Value;
                        offAt ??= confirmOff.Value;
                        open = null;
                    }
                    else if (back.HasValue && v1 > v0)
                    {
                        var dur = back.Value - open.StartMs;
                        var cls = dur < th.MinStableMs ? "glitch" : "violation";
                        excursions.Add(open with
                        {
                            EndMs = back.Value,
                            DurationMs = dur,
                            Classification = cls,
                            Rule = $"fall-excursion duration {dur:F3} ms {(dur < th.MinStableMs ? "<" : ">=")} min_stable {th.MinStableMs} ms => {cls} (fall threshold {th.FallV} V, hysteresis -{th.HystV} V)"
                        });
                        open = null;
                    }
                }
            }
        }
        if (open is not null)
        {
            // excursion still open at end of capture: unknown duration, keep as candidate data
            diags.Add(new Diagnostic("excursion.open", $"{open.Direction} 越界在采样结束时仍未恢复，持续时间未知", null));
        }
        CloseWindow(pts[^1].TMs, false);
        return new RailAnalysis(wf.Rail, windows, excursions, goodAt, offAt, diags);
    }
}
