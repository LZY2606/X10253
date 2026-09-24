namespace PowerSequenceEvidence.Domain;

public static class RailStateMachine
{
    public sealed record RailStateOutput(
        IReadOnlyList<RailStateWindow> Windows,
        IReadOnlyList<EvidenceEvent> Events,
        RailStateWindow? FirstRising,
        RailStateWindow? FirstFalling);

    private enum RawState { Inactive, Unknown, Active }

    public static RailStateOutput Analyze(
        RailWaveform waveform,
        ThresholdSet threshold,
        MeasurementErrorSpec error,
        IReadOnlyList<string> directSources,
        string caseId,
        string algorithm,
        string fingerprint)
    {
        var windows = new List<RailStateWindow>();
        var events = new List<EvidenceEvent>();
        var start = 0;

        while (start < waveform.Samples.Count)
        {
            var sample = waveform.Samples[start];
            var state = Classify(sample, threshold, error);
            var end = start;
            while (end + 1 < waveform.Samples.Count &&
                   Classify(waveform.Samples[end + 1], threshold, error) == state)
                end++;

            var startSample = waveform.Samples[start];
            var endSample = waveform.Samples[end];
            var duration = DurationSeconds(waveform, start, end, state);
            var stable = duration >= threshold.MinimumStableSeconds;
            var stateName = state switch
            {
                RawState.Active => "ACTIVE",
                RawState.Inactive => "INACTIVE",
                _ => "UNKNOWN"
            };
            var reason = stable ? null : $"DURATION_{duration.ToString("R")}s_BELOW_MINIMUM_{threshold.MinimumStableSeconds.ToString("R")}s";
            var status = state == RawState.Unknown
                ? AnalysisStatus.Unknown
                : stable ? AnalysisStatus.Accepted : AnalysisStatus.Rejected;
            var identityInput = $"window|{caseId}|{waveform.RailId}|{threshold.Version}|{startSample.TimeRaw}|{endSample.TimeRaw}|{stateName}|{reason ?? "stable"}";
            var window = new RailStateWindow(
                StableIdentity.Short("win", identityInput),
                waveform.RailId,
                stateName,
                start,
                end,
                startSample.TimeConvertible ? startSample.TimeSeconds : 0,
                endSample.TimeConvertible ? endSample.TimeSeconds : 0,
                state == RawState.Active,
                directSources,
                status,
                reason,
                StableIdentity.Short("disp", $"display|{identityInput}"));
            windows.Add(window);

            var severity = status switch
            {
                AnalysisStatus.Accepted when state == RawState.Active => EventSeverity.Info,
                AnalysisStatus.Rejected => EventSeverity.Rejected,
                AnalysisStatus.Unknown => EventSeverity.Unknown,
                _ => EventSeverity.Info
            };
            var type = state == RawState.Unknown
                ? "rail.unknown"
                : stable
                    ? state == RawState.Active ? "rail.stable-active" : "rail.stable-inactive"
                    : state == RawState.Active ? "rail.active-glitch" : "rail.inactive-glitch";
            var message = state == RawState.Unknown
                ? $"{waveform.RailId} 位于上升阈值 {threshold.RisingRaw} 与下降阈值 {threshold.FallingRaw} 之间，状态未知。"
                : stable
                        ? $"{waveform.RailId} 稳定为 {stateName}。"
                        : $"{waveform.RailId} {stateName} 仅持续 {duration.ToString("0.###E+00")}s，按 glitch 拒绝。";
            events.Add(new EvidenceEvent(
                StableIdentity.Short("evt", $"event|{window.StableIdentity}|{type}"),
                caseId,
                type,
                severity,
                status,
                message,
                startSample.TimeConvertible ? startSample.TimeSeconds : null,
                endSample.TimeConvertible ? endSample.TimeSeconds : null,
                waveform.RailId,
                null,
                null,
                directSources,
                algorithm,
                fingerprint,
                new Dictionary<string, string>
                {
                    ["durationSeconds"] = duration.ToString("R"),
                    ["minimumStableSeconds"] = threshold.MinimumStableSeconds.ToString("R"),
                    ["risingThresholdVolts"] = threshold.RisingVolts.ToString("R"),
                    ["fallingThresholdVolts"] = threshold.FallingVolts.ToString("R"),
                    ["hysteresisVolts"] = threshold.HysteresisVolts.ToString("R"),
                    ["measurementErrorVolts"] = error.VoltageAbsoluteVolts.ToString("R")
                },
                null,
                reason is null ? null : new[] { reason }));
            start = end + 1;
        }

        var rising = windows.FirstOrDefault(w =>
            w is { State: "ACTIVE", Status: AnalysisStatus.Accepted, FromRisingThreshold: true });
        var falling = windows.FirstOrDefault(w =>
            w is { State: "INACTIVE", Status: AnalysisStatus.Accepted });
        return new RailStateOutput(windows, events, rising, falling);
    }

    private static RawState Classify(RailSample sample, ThresholdSet threshold, MeasurementErrorSpec error)
    {
        if (!sample.VoltageConvertible)
            return RawState.Unknown;
        var low = threshold.FallingVolts - error.VoltageAbsoluteVolts;
        var high = threshold.RisingVolts + error.VoltageAbsoluteVolts;
        if (sample.VoltageVolts <= low)
            return RawState.Inactive;
        if (sample.VoltageVolts >= high)
            return RawState.Active;
        return RawState.Unknown;
    }

    private static double DurationSeconds(RailWaveform waveform, int start, int end, RawState state)
    {
        if (!waveform.Samples[start].TimeConvertible || !waveform.Samples[end].TimeConvertible)
            return double.PositiveInfinity;
        var nominal = waveform.Samples[end].TimeSeconds - waveform.Samples[start].TimeSeconds;
        if (end + 1 < waveform.Samples.Count && waveform.Samples[end + 1].TimeConvertible)
            return waveform.Samples[end + 1].TimeSeconds - waveform.Samples[start].TimeSeconds;
        if (start > 0 && waveform.Samples[start - 1].TimeConvertible)
            return waveform.Samples[end].TimeSeconds - waveform.Samples[start - 1].TimeSeconds;
        return nominal;
    }
}
