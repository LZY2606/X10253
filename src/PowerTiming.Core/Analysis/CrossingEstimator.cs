using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Analysis;

/// <summary>
/// Estimates threshold instants from preserved samples without pretending
/// uncertainty is zero. Error envelopes, sample-grid quantization, and clock
/// gaps become Known / Ambiguous / Unknown first-class results.
/// </summary>
public sealed class CrossingEstimator
{
    private readonly ClockModel _clock;

    public CrossingEstimator(ClockModel clock) => _clock = clock;

    public CrossingEstimate? Estimate(RailInput rail, string phase, bool rising)
    {
        var threshold = rising ? rail.RiseThreshold : rail.FallThreshold;
        var kind = rising ? "rise" : "fall";
        if (!threshold.NormalizedSi.HasValue)
        {
            return new CrossingEstimate(
                rail.RailId,
                phase,
                kind,
                double.NaN,
                null,
                null,
                null,
                EstimateKind.Unknown,
                "threshold-unconvertible-retained",
                [],
                null,
                $"Threshold '{threshold.Raw}' remains at its original position with diagnostic.");
        }

        var thresholdValue = threshold.NormalizedSi.Value;
        var usable = rail.Samples
            .Where(sample => IsNumeric(sample.Time.NormalizedSi) && IsNumeric(sample.Voltage.NormalizedSi))
            .OrderBy(sample => sample.Time.NormalizedSi!.Value)
            .ToList();

        if (usable.Count < 2)
        {
            return new CrossingEstimate(rail.RailId, phase, kind, thresholdValue, null, null, null, EstimateKind.Unknown,
                "insufficient-numeric-samples", [], null, "No numeric bracket can be formed; raw samples are retained.");
        }

        for (var index = 1; index < usable.Count; index++)
        {
            var a = usable[index - 1];
            var b = usable[index];
            if (BridgesGap(a, b, out var gapId))
            {
                if (BracketsThreshold(a, b, thresholdValue, rising))
                {
                    return new CrossingEstimate(rail.RailId, phase, kind, thresholdValue, null, null, null, EstimateKind.Unknown,
                        "threshold-crossing-inside-clock-gap", new[] { a.StableSampleId, b.StableSampleId }, gapId,
                        "No sample exists in the clock gap, so the crossing interval cannot be ordered beyond Unknown.");
                }

                continue;
            }

            if (!BracketsThreshold(a, b, thresholdValue, rising))
            {
                continue;
            }

            var va = a.Voltage.NormalizedSi!.Value;
            var vb = b.Voltage.NormalizedSi!.Value;
            var ta = a.Time.NormalizedSi!.Value;
            var tb = b.Time.NormalizedSi!.Value;
            var fraction = (thresholdValue - va) / (vb - va);
            var nominal = ta + fraction * (tb - ta);

            var earlyThreshold = thresholdValue + EffectiveVoltageError(a, b, positive: rising);
            var lateThreshold = thresholdValue - EffectiveVoltageError(a, b, positive: rising);
            var earlyFraction = Clamp01((earlyThreshold - va) / (vb - va));
            var lateFraction = Clamp01((lateThreshold - va) / (vb - va));
            if (!rising)
            {
                (earlyFraction, lateFraction) = (lateFraction, earlyFraction);
            }

            var timeError = EffectiveTimeError(a, b);
            var earliest = ta + earlyFraction * (tb - ta) - timeError;
            var latest = tb - (1 - lateFraction) * (tb - ta) + timeError;
            earliest = Math.Min(earliest, nominal);
            latest = Math.Max(latest, nominal);

            return new CrossingEstimate(
                rail.RailId,
                phase,
                kind,
                thresholdValue,
                nominal,
                earliest,
                latest,
                EstimateKind.Known,
                "linear-interpolation-plus-error-envelope",
                [a.StableSampleId, b.StableSampleId],
                null,
                null);
        }

        return new CrossingEstimate(rail.RailId, phase, kind, thresholdValue, null, null, null, EstimateKind.Unknown,
            "threshold-never-bracketed", [], null, "Preserved waveform does not bracket the requested threshold.");
    }

    private static bool BracketsThreshold(VoltageSample a, VoltageSample b, double threshold, bool rising)
    {
        var va = a.Voltage.NormalizedSi!.Value;
        var vb = b.Voltage.NormalizedSi!.Value;
        return rising ? va < threshold && vb >= threshold : va >= threshold && vb < threshold;
    }

    private bool BridgesGap(VoltageSample a, VoltageSample b, out string? gapId)
    {
        gapId = null;
        var ta = a.Time.NormalizedSi!.Value;
        var tb = b.Time.NormalizedSi!.Value;
        var gapIndex = 0;
        foreach (var gap in _clock.Gaps)
        {
            if (gap.Start.NormalizedSi is { } start && gap.End.NormalizedSi is { } end && ta <= start && tb >= end)
            {
                gapId = $"gap-{gapIndex}";
                return true;
            }

            gapIndex++;
        }

        if (_clock.NominalPeriod.NormalizedSi is { } period && tb - ta > period * 2.5)
        {
            gapId = "gap-inferred";
            return true;
        }

        return false;
    }

    private double EffectiveVoltageError(VoltageSample a, VoltageSample b, bool positive)
    {
        var values = new[]
        {
            a.MeasurementError?.NormalizedSi,
            b.MeasurementError?.NormalizedSi,
            _clock.VoltageMeasurementError?.NormalizedSi
        }.Where(value => value.HasValue).Select(value => value!.Value);

        var error = values.Any() ? values.Max() : 0.0;
        return positive ? error : error;
    }

    private double EffectiveTimeError(VoltageSample a, VoltageSample b)
    {
        var explicitError = new[] { _clock.TimeMeasurementError?.NormalizedSi }.FirstOrDefault(value => value.HasErrorValue()) ?? 0.0;
        var quantization = _clock.NominalPeriod.NormalizedSi ?? 0.0;
        return Math.Max(explicitError, quantization / 2.0);
    }

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    private static bool IsNumeric(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
}

internal static class ErrorExtensions
{
    public static bool HasErrorValue(this double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
}
