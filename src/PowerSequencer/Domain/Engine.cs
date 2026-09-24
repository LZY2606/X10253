using System.Globalization;

namespace PowerSequencer.Domain;

/// <summary>
/// Deterministic sequencing analyzer. Rise and fall use different thresholds;
/// a short excursion is a glitch or a violation decided by its duration
/// against min-stable-time; hysteresis widens the deadband so ripple does not
/// chatter the state machine.
/// </summary>
public static class Engine
{
    public const string AlgoVersion = "seq-analyzer/1.1.0";

    internal static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Canonical fingerprint fragment for a quantity (normalized when possible).</summary>
    public static string QuantityFingerprint(Quantity q, UnitKind kind) =>
        Units.TryNormalize(q, kind, out var c, out _) ? $"{kind}:{F(c)}" : $"{kind}:raw:{q}";

    public static string RailFingerprint(RailConstraint rc) => Hashing.Sha256(string.Join("|",
        rc.Rail.ToUpperInvariant(),
        QuantityFingerprint(rc.RiseThreshold, UnitKind.Voltage),
        QuantityFingerprint(rc.FallThreshold, UnitKind.Voltage),
        QuantityFingerprint(rc.Hysteresis, UnitKind.Voltage),
        QuantityFingerprint(rc.MinStableTime, UnitKind.Time)));

    public static string EdgesFingerprint(List<DependencyEdge> edges) => Hashing.Sha256(string.Join(";",
        edges.Select(e => $"{e.From}->{e.To}:{e.Kind}:{(e.MaxDelay is null ? "-" : QuantityFingerprint(e.MaxDelay, UnitKind.Time))}")
             .OrderBy(s => s, StringComparer.Ordinal)));

    /// <summary>Timing uncertainty of one signal: one sample period plus jitter.</summary>
    public static double SignalTolerance(WaveformData wf, List<Diagnostic>? diags = null)
    {
        var okP = Units.TryNormalize(wf.SampleClock, UnitKind.Time, out var period, out var e1);
        var okJ = Units.TryNormalize(wf.ClockJitter, UnitKind.Time, out var jitter, out var e2);
        if (!okP) diags?.Add(new("bad-unit", $"sample clock of {wf.Rail}: {e1}", wf.Rail, null));
        if (!okJ) diags?.Add(new("bad-unit", $"clock jitter of {wf.Rail}: {e2}", wf.Rail, null));
        return (okP ? period : 0) + (okJ ? jitter : 0);
    }

    /// <summary>Analyze one rail/signal waveform against its constraint (two passes).</summary>
    public static RailAnalysisResult AnalyzeRail(
        WaveformData wf, RailConstraint rc, string constraintSetId, int constraintVersion, string streamId, int streamVersion)
    {
        var diags = new List<Diagnostic>();
        var sources = new List<SourceRef>
        {
            new("waveform", streamId, streamVersion),
            new("constraints", $"{constraintSetId}:{rc.Rail}", constraintVersion),
        };

        var okRise = Units.TryNormalize(rc.RiseThreshold, UnitKind.Voltage, out var riseTh, out var eRise);
        var okFall = Units.TryNormalize(rc.FallThreshold, UnitKind.Voltage, out var fallTh, out var eFall);
        var okHyst = Units.TryNormalize(rc.Hysteresis, UnitKind.Voltage, out var hyst, out var eHyst);
        var okMin = Units.TryNormalize(rc.MinStableTime, UnitKind.Time, out var minStable, out var eMin);
        if (!okRise) diags.Add(new("bad-unit", $"rise threshold: {eRise}", wf.Rail, null));
        if (!okFall) diags.Add(new("bad-unit", $"fall threshold: {eFall}", wf.Rail, null));
        if (!okHyst) diags.Add(new("bad-unit", $"hysteresis: {eHyst}", wf.Rail, null));
        if (!okMin) diags.Add(new("bad-unit", $"min stable time: {eMin}", wf.Rail, null));
        var okMeas = Units.TryNormalize(wf.MeasurementError, UnitKind.Voltage, out var measErr, out var eMeas);
        if (!okMeas) diags.Add(new("bad-unit", $"measurement error: {eMeas}", wf.Rail, null));
        if (!okMeas) measErr = 0;

        var timingErr = SignalTolerance(wf, diags);
        Units.TryNormalize(wf.SampleClock, UnitKind.Time, out var period, out _);
        Units.TryNormalize(wf.ClockJitter, UnitKind.Time, out var jitter, out _);

        // Hysteresis widens the deadband symmetrically around both thresholds.
        var effRise = riseTh + hyst / 2;
        var effFall = fallTh - hyst / 2;
        if (okRise && okFall && okHyst && effFall >= effRise)
            diags.Add(new("hysteresis-exceeds-band",
                $"effective fall {F(effFall)}V >= effective rise {F(effRise)}V; state machine degenerate", wf.Rail, null));

        // Normalize samples; unconvertible points stay in place as diagnostics.
        var pts = new List<(double t, double v)>();
        foreach (var s in wf.Samples)
        {
            var okT = Units.TryNormalize(s.Time, UnitKind.Time, out var t, out var et);
            var okV = Units.TryNormalize(s.Value, UnitKind.Voltage, out var v, out var ev);
            if (!okT) diags.Add(new("unconvertible-sample", $"time kept raw '{s.Time}': {et}", wf.Rail, null));
            if (!okV) diags.Add(new("unconvertible-sample", $"value kept raw '{s.Value}': {ev}", wf.Rail, null));
            if (okT && okV) pts.Add((t, v));
        }
        pts.Sort((a, b) => a.t.CompareTo(b.t));

        var fingerprint = RailFingerprint(rc);
        if (pts.Count == 0)
        {
            diags.Add(new("no-usable-samples", "no convertible samples", wf.Rail, null));
            return new RailAnalysisResult(wf.Rail, AlgoVersion, fingerprint, sources,
                new List<StateWindow>(), new List<Excursion>(), diags);
        }

        // ---- Pass 1: hysteresis state machine -> raw windows (gaps become Unknown) ----
        var raw = new List<StateWindow>();
        RailState Classify(double v, RailState previous)
        {
            if (v >= effRise) return RailState.High;
            if (v <= effFall) return RailState.Low;
            return previous; // hysteresis deadband: keep previous state
        }

        var state = pts[0].v >= effRise ? RailState.High
                  : pts[0].v <= effFall ? RailState.Low
                  : RailState.Unknown;
        if (state == RailState.Unknown)
            diags.Add(new("initial-indeterminate", "first sample inside hysteresis deadband", wf.Rail, pts[0].t));

        var winStart = pts[0].t;
        for (var i = 1; i < pts.Count; i++)
        {
            var (tPrev, _) = pts[i - 1];
            var (t, v) = pts[i];
            var dt = t - tPrev;
            if (dt > period + jitter + 1e-12)
            {
                raw.Add(new StateWindow(wf.Rail, state, winStart, tPrev, true, "before-clock-gap"));
                raw.Add(new StateWindow(wf.Rail, RailState.Unknown, tPrev, t, false,
                    $"clock-gap: dt={F(dt)}s exceeds period+jitter={F(period + jitter)}s"));
                diags.Add(new("clock-gap", $"gap of {F(dt)}s between samples", wf.Rail, tPrev));
                state = Classify(v, state);
                winStart = t;
                continue;
            }
            var next = Classify(v, state);
            if (next != state)
            {
                var certain = Math.Abs(v - effRise) > measErr && Math.Abs(v - effFall) > measErr;
                raw.Add(new StateWindow(wf.Rail, state, winStart, t, certain, "threshold-crossing"));
                if (!certain)
                    diags.Add(new("uncertain-crossing",
                        $"crossing within measurement error {F(measErr)}V of a threshold", wf.Rail, t));
                state = next;
                winStart = t;
            }
        }
        raw.Add(new StateWindow(wf.Rail, state, winStart, pts[^1].t, true, "end-of-capture"));

        // ---- Pass 2: short opposite-state windows become glitch / violation / ambiguous ----
        var windows = new List<StateWindow>();
        var excursions = new List<Excursion>();
        for (var i = 0; i < raw.Count; i++)
        {
            var w = raw[i];
            var prev = i > 0 ? raw[i - 1] : null;
            var next = i < raw.Count - 1 ? raw[i + 1] : null;

            var oppositeOfNeighbors =
                w.State != RailState.Unknown &&
                prev is not null && next is not null &&
                prev.State == next.State &&
                prev.State != RailState.Unknown &&
                w.State != prev.State;

            if (!oppositeOfNeighbors)
            {
                // Unterminated excursion: final window is opposite of the settled state.
                if (i == raw.Count - 1 && i > 0 && w.State != RailState.Unknown &&
                    prev is not null && prev.State != RailState.Unknown && w.State != prev.State)
                {
                    var dur0 = w.TEnd - w.TStart;
                    excursions.Add(new Excursion(w.Rail, w.State == RailState.Low ? "dip" : "spike",
                        w.TStart, w.TEnd, dur0, ExcursionClass.Unknown,
                        new List<string> { ExcursionClass.Glitch, ExcursionClass.Violation },
                        "excursion still open at end of capture; duration unknown"));
                    diags.Add(new("unterminated-excursion", "capture ends inside an excursion", wf.Rail, w.TStart));
                }
                windows.Add(w);
                continue;
            }

            var dur = w.TEnd - w.TStart;
            var dir = w.State == RailState.Low ? "dip" : "spike";
            string cls;
            List<string> candidates;
            string reason;
            if (dur + timingErr < minStable)
            {
                cls = ExcursionClass.Glitch;
                candidates = new List<string> { ExcursionClass.Glitch };
                reason = $"duration {F(dur)}s + timing error {F(timingErr)}s < min stable {F(minStable)}s";
            }
            else if (dur - timingErr >= minStable)
            {
                cls = ExcursionClass.Violation;
                candidates = new List<string> { ExcursionClass.Violation };
                reason = $"duration {F(dur)}s - timing error {F(timingErr)}s >= min stable {F(minStable)}s";
            }
            else
            {
                cls = ExcursionClass.Ambiguous;
                candidates = new List<string> { ExcursionClass.Glitch, ExcursionClass.Violation };
                reason = $"duration {F(dur)}s within timing error {F(timingErr)}s of min stable {F(minStable)}s; cannot decide uniquely";
            }
            excursions.Add(new Excursion(w.Rail, dir, w.TStart, w.TEnd, dur, cls, candidates, reason));

            if (cls == ExcursionClass.Glitch)
            {
                // Glitch: merge back into the surrounding state (no committed change).
                if (windows.Count > 0 && windows[^1].State == prev!.State)
                    windows[^1] = windows[^1] with { TEnd = w.TEnd, Reason = windows[^1].Reason + ";glitch-merged" };
                else
                    windows.Add(new StateWindow(w.Rail, prev!.State, w.TStart, w.TEnd, false, "glitch-window"));
            }
            else
            {
                // Violation / ambiguous / unknown: keep the window, flag certainty.
                windows.Add(w with { Certain = cls == ExcursionClass.Violation && w.Certain, Reason = w.Reason + $";{cls}" });
            }
        }

        return new RailAnalysisResult(wf.Rail, AlgoVersion, fingerprint, sources, windows, excursions, diags);
    }

    // ---------------- Sequence analysis (startup / shutdown order) ----------------

    public sealed record EdgeTime(string Rail, double Time, double Tolerance);

    private static List<SequenceStep> GroupSimultaneous(List<EdgeTime> events, string basis)
    {
        var sorted = events.OrderBy(e => e.Time).ToList();
        var steps = new List<SequenceStep>();
        var i = 0;
        while (i < sorted.Count)
        {
            var group = new List<string> { sorted[i].Rail };
            var t0 = sorted[i].Time;
            var j = i + 1;
            while (j < sorted.Count &&
                   sorted[j].Time - sorted[j - 1].Time <= sorted[j].Tolerance + sorted[j - 1].Tolerance + 1e-12)
            {
                group.Add(sorted[j].Rail);
                j++;
            }
            group.Sort(StringComparer.Ordinal);
            steps.Add(new SequenceStep(group, t0, group.Count > 1 ? $"{basis};simultaneous-within-tolerance" : basis));
            i = j;
        }
        return steps;
    }

    /// <summary>All legal orderings: permutations inside simultaneous groups (capped).</summary>
    public static List<List<string>> AllOrderings(List<SequenceStep> steps, int cap = 16)
    {
        var result = new List<List<string>> { new() };
        foreach (var step in steps)
        {
            var perms = Permutations(step.Rails).ToList();
            var next = new List<List<string>>();
            foreach (var acc in result)
                foreach (var p in perms)
                {
                    var combined = new List<string>(acc);
                    combined.AddRange(p);
                    next.Add(combined);
                }
            result = next;
            if (result.Count > cap)
                return result.Take(cap).ToList();
        }
        return result;
    }

    private static IEnumerable<List<string>> Permutations(List<string> items)
    {
        if (items.Count <= 1) { yield return new List<string>(items); yield break; }
        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, k) => k != i).ToList();
            foreach (var p in Permutations(rest))
            {
                var l = new List<string> { items[i] };
                l.AddRange(p);
                yield return l;
            }
        }
    }

    public static SequenceAnalysisResult AnalyzeSequence(
        List<EdgeTime> rises, List<EdgeTime> falls, List<SourceRef> sources, string paramFingerprint)
    {
        var diags = new List<Diagnostic>();
        var startup = GroupSimultaneous(rises, "first rise above effective rise threshold");
        var shutdown = GroupSimultaneous(falls, "first fall below effective fall threshold");

        var startupCandidates = AllOrderings(startup);
        if (startupCandidates.Count > 1)
            diags.Add(new("simultaneous-arrival",
                $"startup order ambiguous: {startupCandidates.Count} legal orderings kept", null, startup.FirstOrDefault()?.Time));

        // Shutdown must be the exact reverse of startup. Check every legal startup
        // candidate; a mismatch that holds for ALL candidates is a hard violation.
        var mismatches = new List<ShutdownMismatch>();
        var actualFlat = shutdown.SelectMany(s => s.Rails).ToList();
        if (startupCandidates.Count > 0 && actualFlat.Count == startupCandidates[0].Count && actualFlat.Count > 0)
        {
            var violatedForAll = true;
            ShutdownMismatch? first = null;
            foreach (var candidate in startupCandidates)
            {
                var expected = candidate.AsEnumerable().Reverse().ToList();
                var idx = -1;
                for (var k = 0; k < expected.Count; k++)
                    if (!string.Equals(expected[k], actualFlat[k], StringComparison.Ordinal)) { idx = k; break; }
                if (idx < 0) { violatedForAll = false; }
                else
                {
                    first ??= new ShutdownMismatch(idx,
                        expected.Skip(idx).ToList(), actualFlat.Skip(idx).ToList(),
                        shutdown.SelectMany(s => s.Rails.Select(r => (rail: r, s.Time)))
                                .FirstOrDefault(x => x.rail == actualFlat[idx]).Time,
                        $"shutdown must be reverse of startup; position {idx}: expected {expected[idx]}, got {actualFlat[idx]}");
                }
            }
            if (violatedForAll && first is not null)
                mismatches.Add(first);
            else if (first is not null)
                diags.Add(new("shutdown-ambiguous",
                    "shutdown order mismatches some but not all legal startup candidates", null, first.At));
        }
        else if (actualFlat.Count != (startupCandidates.FirstOrDefault()?.Count ?? 0))
        {
            diags.Add(new("shutdown-incomplete", "not all rails observed falling; shutdown order unknown", null, null));
        }

        return new SequenceAnalysisResult(AlgoVersion, paramFingerprint, sources,
            startup, shutdown, startupCandidates, mismatches, diags);
    }
}
