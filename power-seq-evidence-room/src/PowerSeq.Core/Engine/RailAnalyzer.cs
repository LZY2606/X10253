using PowerSeq.Core.Model;

namespace PowerSeq.Core.Engine;

public enum Phase { Off, Rising, On, Falling, Fallen }

/// <summary>
/// 单轨滞回状态机。
/// 进入窗口用 riseThreshold，离开用 fallThreshold；minStable 决定短越界是 glitch 还是违约。
/// 未知拍（时钟缺口/不可解析值）冻结候选；跨缺口确认把 UNKNOWN 写入证据。
/// </summary>
public sealed class RailAnalyzer
{
    private readonly NormCapture _cap;
    private readonly int _col;
    private readonly RailSpec _spec;
    private readonly double _rise, _fall, _minStable, _err, _clockErr;
    private readonly List<string> _diag = new();

    public RailAnalyzer(NormCapture cap, int col, RailSpec spec)
    {
        _cap = cap; _col = col; _spec = spec;
        _rise = MustVoltage(spec.RiseThreshold);
        _fall = MustVoltage(spec.FallThreshold);
        _minStable = MustTime(spec.MinStable);
        _err = cap.ErrorVolts[col];
        _clockErr = cap.ClockErrorSeconds;
        if (_rise < _fall) _diag.Add($"轨 {spec.Rail}: rise {_rise}V < fall {_fall}V，滞回方向反转");
    }

    public IReadOnlyList<string> Diagnostics => _diag;

    public (RailTimeline timeline, List<Finding> findings) Run()
    {
        var excursions = new List<Excursion>();
        var findings = new List<Finding>();
        var segments = new List<RailSegment>();
        var n = _cap.Samples.Count;

        var state = Phase.Off;
        var runStart = -1;       // 当前连续高/低候选段首拍
        var runTouchedUnknown = false;
        var riseConfirmed = false;
        var fallConfirmed = false;
        Crossing? riseCrossing = null, fallCrossing = null;
        int riseIdx = -1, fallIdx = -1;

        double V(int r) => _cap.Samples[r].Values[_col]!.Value;
        bool Unknown(int r) => _cap.Samples[r].Values[_col] is null;
        bool High(int r) => V(r) >= _rise;
        bool Low(int r) => V(r) < _fall;
        bool Stable(int s, int r) => (r - s) >= 1 && Span(s, r) + 2 * _clockErr >= _minStable - 1e-12;

        void CloseExcursion(string dir, ExcursionKind kind, int first, int last, bool touched)
        {
            if (last < first) return;
            var dur = Math.Max(0, Span(first, last));
            var disposition = dur + 2 * _clockErr >= _minStable - 1e-12
                ? Disposition.Violation : Disposition.Glitch;
            var threshold = kind == ExcursionKind.RiseDip ? _fall : _rise;
            var exc = new Excursion(_spec.Rail, dir, kind, disposition, dur, threshold, _minStable,
                first, last, Window(first, last),
                Derive("excursion", ("rail", _spec.Rail), ("firstIndex", first.ToString()), ("lastIndex", last.ToString())));
            excursions.Add(exc);
            findings.Add(ExcursionFinding(exc, touched));
        }

        for (var r = 0; r < n; r++)
        {
            if (Unknown(r)) { runTouchedUnknown = true; continue; }

            switch (state)
            {
                case Phase.Off:
                    if (High(r)) { state = Phase.Rising; runStart = r; runTouchedUnknown = false; }
                    break;

                case Phase.Rising:
                    if (High(r))
                    {
                        if (Stable(runStart, r))
                        {
                            state = Phase.On; riseConfirmed = true; riseIdx = runStart;
                            riseCrossing = MakeCrossing(riseIdx, "rise");
                            segments.Add(new RailSegment(_spec.Rail, RailState.On, T(riseIdx), T(r), riseIdx, r));
                        }
                    }
                    else if (Low(r))
                    {
                        // 稳定前回穿：无上升事件，无 glitch（尚未进入窗口）
                        state = Phase.Off; runStart = -1;
                    }
                    break;

                case Phase.On:
                    if (Low(r)) { state = Phase.Falling; runStart = r; }
                    break;

                case Phase.Falling:
                    if (Low(r))
                    {
                        if (Stable(runStart, r))
                        {
                            state = Phase.Fallen; fallConfirmed = true; fallIdx = runStart;
                            fallCrossing = MakeCrossing(fallIdx, "fall");
                            segments.Add(new RailSegment(_spec.Rail, RailState.Falling, T(fallIdx), T(r), fallIdx, r));
                            runStart = -1;
                        }
                    }
                    else if (High(r))
                    {
                        CloseExcursion("rise", ExcursionKind.RiseDip, runStart, r - 1, runTouchedUnknown);
                        state = Phase.On; runStart = -1; runTouchedUnknown = false;
                    }
                    break;

                case Phase.Fallen:
                    // 下降已确认：回升为 fall-pop。长 pop=违约并重新 On；短 pop=glitch 后仍按 Fallen 观察
                    if (High(r))
                    {
                        if (runStart < 0) { runStart = r; runTouchedUnknown = false; }
                        if (Stable(runStart, r))
                        {
                            CloseExcursion("fall", ExcursionKind.FallPop, runStart, r, runTouchedUnknown);
                            state = Phase.On; runStart = -1; runTouchedUnknown = false;
                        }
                    }
                    else if (Low(r))
                    {
                        if (runStart >= 0)
                        {
                            CloseExcursion("fall", ExcursionKind.FallPop, runStart, r - 1, runTouchedUnknown);
                            runStart = -1; runTouchedUnknown = false;
                        }
                    }
                    break;
            }
        }

        // 流末未闭合候选
        if (state == Phase.Falling && runStart >= 0)
            CloseExcursion("rise", ExcursionKind.RiseDip, runStart, n - 1, runTouchedUnknown);
        if (state == Phase.Fallen && runStart >= 0)
            CloseExcursion("fall", ExcursionKind.FallPop, runStart, n - 1, runTouchedUnknown);

        var timeline = new RailTimeline(_spec.Rail, segments, riseCrossing, fallCrossing, excursions);
        return (timeline, findings);
    }

    private double Span(int a, int b) => Math.Max(0, T(b) - T(a));

    private double T(int row)
    {
        var t = _cap.Samples[row].TimeSeconds;
        return t ?? row * _cap.NominalPeriodSeconds;
    }

    private Interval Window(int first, int last) =>
        new(T(first) - _clockErr, T(first) + _clockErr, T(last) - _clockErr, T(last) + _clockErr);

    private Crossing MakeCrossing(int index, string dir)
    {
        var t = T(index);
        var dt = _clockErr + _cap.NominalPeriodSeconds / 2.0 + SlopeTime(index);
        return new Crossing(_spec.Rail, _spec.Rail, dir, new Interval(t - dt, t + dt, t - dt, t + dt),
            index, Derive("crossing", ("rail", _spec.Rail), ("index", index.ToString()), ("dir", dir)));
    }

    private double SlopeTime(int index)
    {
        if (_err <= 0) return 0;
        var prev = NearestKnown(index, -1);
        var next = NearestKnown(index, +1);
        if (prev < 0 || next < 0) return _cap.NominalPeriodSeconds / 2.0;
        var dv = Math.Abs(V(next) - V(prev));
        var dt = Math.Abs(T(next) - T(prev));
        if (dv <= 1e-12 || dt <= 0) return _cap.NominalPeriodSeconds / 2.0;
        return Math.Min(_cap.NominalPeriodSeconds, _err / (dv / dt));
    }

    private int NearestKnown(int from, int step)
    {
        var r = from + step;
        while (r >= 0 && r < _cap.Samples.Count)
        {
            if (_cap.Samples[r].Values[_col] is not null) return r;
            r += step;
        }
        return -1;
    }

    private Finding ExcursionFinding(Excursion exc, bool touched)
    {
        var violation = exc.Disposition == Disposition.Violation;
        var verdict = violation ? FindingVerdict.ViolationObserved : FindingVerdict.GlitchObserved;
        var rejected = violation
            ? $"glitch 候选被拒绝：持续 {Us(exc.DurationSeconds)} ≥ 最小稳定 {Us(exc.MinStableSeconds)}"
            : $"violation 候选被拒绝：持续 {Us(exc.DurationSeconds)} < 最小稳定 {Us(exc.MinStableSeconds)}";
        return new Finding(
            $"fnd:{exc.StableId}",
            FindingKind.Glitch,
            $"{exc.Rail}:{exc.Direction}",
            verdict, "global",
            exc.Window, null, exc.DurationSeconds, exc.MinStableSeconds, null,
            2 * _clockErr,
            new[] { "glitch", "violation" },
            new[] { rejected },
            Array.Empty<string>(),
            new[] { SourceRef.Of(_cap.Raw.CaptureId, _spec.Rail,
                ("kind", "excursion"), ("firstIndex", exc.FirstIndex.ToString()), ("lastIndex", exc.LastIndex.ToString())) },
            exc.Derivation,
            $"{exc.Rail} {exc.Direction} 越界持续 {Us(exc.DurationSeconds)} => {exc.Disposition}"
            + (touched ? "（接触未知拍，证据含 UNKNOWN）" : ""));
    }

    private static string Us(double seconds) =>
        seconds >= 1e-3 ? $"{seconds * 1e3:0.###}ms" : $"{seconds * 1e6:0.##}us";

    private Derivation Derive(string what, params (string, string)[] coords) =>
        new(Derivation.CurrentAlgorithmVersion, "",
            new[] { SourceRef.Of(_cap.Raw.CaptureId, _spec.Rail, coords) });

    private static double MustVoltage(Qty q)
        => UnitTable.ToCanonicalVoltage(q) ?? throw new InvalidOperationException($"参数 '{q.RawText}' 无法换算为电压");

    private static double MustTime(Qty q)
        => UnitTable.ToCanonicalTime(q) ?? throw new InvalidOperationException($"参数 '{q.RawText}' 无法换算为时间");
}
