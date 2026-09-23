using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Analysis;

/// <summary>
/// Per-rail hysteretic state machine.
/// - Rise and fall use distinct thresholds.
/// - In-band samples never change state (hysteresis).
/// - An excursion shorter than min_stable is a glitch; at/above it is a violation.
/// - A clock gap that hides a transition opens an Unknown window instead of guessing.
/// </summary>
public sealed class RailStateMachine
{
    private readonly ClockModel _clock;

    public RailStateMachine(ClockModel clock) => _clock = clock;

    public RailWindowResult Analyze(RailInput rail, string railVersionId, out string parameterFingerprint)
    {
        parameterFingerprint = Hashing.StableId("params",
            $"rail|{rail.RailId}|{rail.RiseThreshold.Raw}|{rail.FallThreshold.Raw}|{rail.HysteresisBand?.Raw}|{rail.MinStable.Raw}|{rail.Samples.Count}|{_clock.NominalPeriod.Raw}",
            20);

        var diagnostics = new List<DiagnosticEvent>();
        foreach (var sample in rail.Samples.Where(sample => !sample.Voltage.IsConvertible || !sample.Time.IsConvertible))
        {
            var bad = !sample.Voltage.IsConvertible ? sample.Voltage : sample.Time;
            diagnostics.Add(new DiagnosticEvent(
                "UNCONVERTIBLE_SAMPLE",
                sample.StableSampleId,
                $"Sample is retained at its original location but is not normalized: {bad.Diagnostic}.",
                bad.Raw,
                "retained-raw-not-plotted-as-voltage"));
        }

        var rise = new CrossingEstimator(_clock).Estimate(rail, "startup", rising: true);
        var fall = new CrossingEstimator(_clock).Estimate(rail, "shutdown", rising: false);

        var windows = new List<StateWindow>();
        var glitches = new List<GlitchEvent>();
        var numeric = rail.Samples
            .Where(sample => sample.Time.NormalizedSi.HasValue && sample.Voltage.NormalizedSi.HasValue)
            .OrderBy(sample => sample.Time.NormalizedSi!.Value)
            .ToList();

        var state = "LOW";
        double? armTime = null;
        double? violationStart = null;
        double windowStart = numeric.Count > 0 ? numeric[0].Time.NormalizedSi!.Value : null;
        double? lastTime = windowStart;
        string lastReason = "initial sample is below fall threshold";

        for (var index = 0; index < numeric.Count; index++)
        {
            var sample = numeric[index];
            var t = sample.Time.NormalizedSi!.Value;
            var v = sample.Voltage.NormalizedSi!.Value;
            var previous = index > 0 ? numeric[index - 1] : null;
            if (previous is not null && SpansGap(previous, sample))
            {
                if (windowStart.HasValue)
                {
                    windows.Add(new StateWindow(state, windowStart, previous.Time.NormalizedSi, "samples", lastReason));
                }

                windows.Add(new StateWindow("UNKNOWN", previous.Time.NormalizedSi, t, "clock-gap", "Samples are absent; threshold transition is unobservable."));
                windowStart = t;
                state = "LOW";
                armTime = null;
                violationStart = null;
                lastReason = "state reset to conservative LOW after clock gap";
            }

            var riseThreshold = rail.RiseThreshold.NormalizedSi;
            var fallThreshold = rail.FallThreshold.NormalizedSi;
            switch (state)
            {
                case "LOW" when riseThreshold.HasValue && v >= riseThreshold.Value:
                    if (windowStart.HasValue)
                    {
                        windows.Add(new StateWindow("LOW", windowStart, t, "samples", lastReason));
                    }

                    state = "ARMED_HIGH";
                    windowStart = t;
                    armTime = t;
                    violationStart = null;
                    lastReason = "crossed distinct rise threshold; stability timer opened";
                    break;
                case "ARMED_HIGH":
                    if (fallThreshold.HasValue && v < fallThreshold.Value)
                    {
                        var duration = t - armTime!.Value;
                        ClassifyExcursion(rail, glitches, armTime.Value, t, duration, "fall-below-during-stability", "rise");
                        windows.Add(new StateWindow("ARMED_HIGH", windowStart, t, "samples", "rise threshold crossed, waiting min_stable"));
                        state = "LOW";
                        windowStart = t;
                        armTime = null;
                        lastReason = "fall threshold broke stability; excursion classified by duration";
                    }
                    else if (rail.MinStable.NormalizedSi is { } minStable && t - armTime!.Value + 1e-15 >= minStable)
                    {
                        windows.Add(new StateWindow("ARMED_HIGH", windowStart, t, "samples", "rise threshold crossed, waiting min_stable"));
                        state = "HIGH";
                        windowStart = t;
                        lastReason = "voltage stayed above fall threshold for min_stable";
                    }

                    break;
                case "HIGH" when fallThreshold.HasValue && v < fallThreshold.Value:
                    if (windowStart.HasValue)
                    {
                        windows.Add(new StateWindow("HIGH", windowStart, t, "samples", lastReason));
                    }

                    state = "ARMED_LOW";
                    windowStart = t;
                    armTime = t;
                    lastReason = "crossed distinct fall threshold; down-state stability timer opened";
                    break;
                case "ARMED_LOW":
                    if (riseThreshold.HasValue && v >= riseThreshold.Value)
                    {
                        var duration = t - armTime!.Value;
                        ClassifyExcursion(rail, glitches, armTime.Value, t, duration, "rise-during-fall-stability", "fall");
                        windows.Add(new StateWindow("ARMED_LOW", windowStart, t, "samples", "fall threshold crossed, waiting min_stable"));
                        state = "HIGH";
                        windowStart = t;
                        armTime = null;
                        lastReason = "rise threshold reclaimed state; down excursion classified by duration";
                    }
                    else if (rail.MinStable.NormalizedSi is { } stable && t - armTime!.Value + 1e-15 >= stable)
                    {
                        windows.Add(new StateWindow("ARMED_LOW", windowStart, t, "samples", "fall threshold crossed, waiting min_stable"));
                        state = "LOW";
                        windowStart = t;
                        lastReason = "voltage stayed below rise threshold for min_stable";
                    }

                    break;
            }

            lastTime = t;
        }

        if (windowStart.HasValue && lastTime.HasValue)
        {
            var terminalReason = state switch
            {
                "ARMED_HIGH" => "waveform ended before min_stable confirmed HIGH",
                "ARMED_LOW" => "waveform ended before min_stable confirmed LOW",
                _ => lastReason
            };
            windows.Add(new StateWindow(state, windowStart, lastTime, "samples", terminalReason));
        }

        double? stableConfirmed = null;
        if (rise?.NominalSeconds.HasValue == true && rail.MinStable.NormalizedSi.HasValue)
        {
            stableConfirmed = rise.NomimalOrEarliest() + rail.MinStable.NormalizedSi.Value;
        }

        var judgments = JudgeSignals(rail, rise, stableConfirmed, fall);

        return new RailWindowResult(
            rail.RailId,
            railVersionId,
            windows,
            rise,
            fall,
            stableConfirmed,
            glitches,
            judgments,
            diagnostics,
            parameterFingerprint);
    }

    private bool SpansGap(VoltageSample a, VoltageSample b)
    {
        if (a.Flags.Contains("gap_after") || b.Flags.Contains("gap_before"))
        {
            return true;
        }

        var ta = a.Time.NormalizedSi!.Value;
        var tb = b.Time.NormalizedSi!.Value;
        foreach (var gap in _clock.Gaps)
        {
            if (gap.Start.NormalizedSi is { } start && gap.End.NormalizedSi is { } end && ta <= start && tb >= end)
            {
                return true;
            }
        }

        return _clock.NominalPeriod.NormalizedSi is { } period && tb - ta > period * 2.5;
    }

    private static void ClassifyExcursion(
        RailInput rail,
        List<GlitchEvent> glitches,
        double start,
        double end,
        double duration,
        string trigger,
        string thresholdKind)
    {
        var minStable = rail.MinStable.NormalizedSi ?? double.PositiveInfinity;
        var classification = duration < minStable ? "glitch" : "violation";
        var id = Hashing.StableId("exc", $"{rail.RailId}|{trigger}|{start:R}|{end:R}|{classification}", 12);
        glitches.Add(new GlitchEvent(
            id,
            rail.RailId,
            thresholdKind,
            start,
            end,
            duration,
            minStable,
            classification,
            "duration < min_stable => glitch; duration >= min_stable => violation",
            []));
    }

    private static List<SignalJudgment> JudgeSignals(RailInput rail, CrossingEstimate? rise, double? stableConfirmed, CrossingEstimate? fall)
    {
        var judgments = new List<SignalJudgment>();
        foreach (var signal in rail.Signals)
        {
            if (!signal.At.NormalizedSi.HasValue)
            {
                judgments.Add(new SignalJudgment(signal.StableSignalId, rail.RailId, signal.Kind, null, null, "unknown", "Signal time is retained but cannot be converted."));
                continue;
            }

            var at = signal.At.NormalizedSi.Value;
            switch (signal.Kind)
            {
                case "good_rise":
                    var refStable = stableConfirmed;
                    var goodStatus = refStable.HasValue && at + 1e-12 < refStable.Value ? "candidate-early-good" : "consistent";
                    judgments.Add(new SignalJudgment(signal.StableSignalId, rail.RailId, signal.Kind, at, refStable, goodStatus,
                        "good_rise may not be asserted before rise crossing + min_stable"));
                    break;
                case "good_fall":
                    judgments.Add(new SignalJudgment(signal.StableSignalId, rail.RailId, signal.Kind, at, fall?.NominalSeconds, "recorded", "good_fall is preserved evidence"));
                    break;
                default:
                    judgments.Add(new SignalJudgment(signal.StableSignalId, rail.RailId, signal.Kind, at, null, "recorded", "enable edge is preserved as sequencing evidence"));
                    break;
            }
        }

        return judgments;
    }
}

internal static class CrossingExtensions
{
    public static double? NomimalOrEarliest(this CrossingEstimate estimate) => estimate.NominalSeconds ?? estimate.EarliestSeconds;
}
