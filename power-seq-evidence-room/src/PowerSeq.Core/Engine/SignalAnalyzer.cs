using PowerSeq.Core.Model;

namespace PowerSeq.Core.Engine;

/// <summary>enable/good 信号：0->1 / 1->0 跳变，窗口含时钟误差与半拍。无滞回。</summary>
public static class SignalAnalyzer
{
    public static (SignalTimeline timeline, List<Finding> findings) Run(NormCapture cap, int col, SignalSpec spec)
    {
        var findings = new List<Finding>();
        Crossing? rise = null, fall = null;
        var n = cap.Samples.Count;
        int? prev = null;
        for (var r = 0; r < n; r++)
        {
            var v = cap.Samples[r].Values[col];
            if (v is null) continue;
            var hi = v.Value >= 0.5;
            if (prev is not null && hi != (prev.Value >= 0.5))
            {
                var dir = hi ? "rise" : "fall";
                var t = cap.Samples[r].TimeSeconds ?? r * cap.NominalPeriodSeconds;
                var dt = cap.ClockErrorSeconds + cap.NominalPeriodSeconds / 2.0;
                var crossing = new Crossing(spec.Rail, spec.Rail, dir,
                    new Interval(t - dt, t + dt, t - dt, t + dt), r,
                    new Derivation(Derivation.CurrentAlgorithmVersion, "",
                        new[] { SourceRef.Of(cap.Raw.CaptureId, spec.Rail, ("kind", "signal"), ("index", r.ToString())) }));
                if (dir == "rise") rise = crossing; else fall = crossing;
            }
            prev = hi ? 1 : 0;
        }
        return (new SignalTimeline(spec.Rail, spec.Kind, rise, fall), findings);
    }
}
