using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerEvidence.Domain;

public sealed record AnalyzerInput(
    CaseSnapshot Snapshot,
    JsonElement? RuleOverrides,
    string RuleVersionId,
    IReadOnlyDictionary<string, RuleVersion> ActiveRules,
    string RunId,
    int Ordinal,
    string ParentEventId);

public static class AnalysisEngine
{
    public const string AlgorithmVersion = "power-hysteresis-state-machine-1.0.0";

    public static AnalysisRun Analyze(AnalyzerInput input)
    {
        var results = new List<AnalysisResult>();
        var effective = BuildEffectiveRules(input.Snapshot, input.RuleOverrides);

        foreach (var signal in input.Snapshot.Signals)
        {
            var ruleVersion = ChooseRuleVersion(input.ActiveRules, new[] { signal.Id });
            var (windows, edges, excursions, clockGaps) = EvaluateSignal(signal, input.Snapshot.Clocks, effective);

            foreach (var gap in clockGaps)
                results.Add(BuildResult(
                    "clock-gap",
                    $"Clock gap on {signal.Id}",
                    ResultStatus.Candidate,
                    signal.Id,
                    gap.Time,
                    $"samples {gap.BeforeSampleId}..{gap.AfterSampleId}; missing interval {gap.GapSeconds:R} s exceeds {gap.LimitSeconds:R} s",
                    null,
                    $"gap duration may contain unknown rail state",
                    windows,
                    edges,
                    Array.Empty<IReadOnlyList<string>>(),
                    new[] { signal.SourceVersionId, signal.ClockId is null ? string.Empty : input.Snapshot.Clocks.First(c => c.Id == signal.ClockId).SourceVersionId },
                    ruleVersion,
                    new[] { signal.Id }));

            foreach (var excursion in excursions)
            {
                var violation = excursion.Duration >= (signal.MinimumStable.Numeric ?? 0);
                results.Add(BuildResult(
                    "threshold-excursion",
                    $"{signal.Id} {(violation ? "violates" : "glitch within")} stability window",
                    violation ? ResultStatus.Candidate : ResultStatus.Rejected,
                    signal.Id,
                    excursion.StartTime,
                    $"{signal.Id} leaves {excursion.ExpectedState} at {excursion.StartSampleId}; duration {excursion.Duration:R} s",
                    violation ? null : $"duration below minimum stable {signal.MinimumStable.Original}; retained as rejected glitch",
                    null,
                    windows,
                    edges,
                    Array.Empty<IReadOnlyList<string>>(),
                    new[] { signal.SourceVersionId },
                    ruleVersion,
                    new[] { signal.Id }));
            }
        }

        var signalMap = input.Snapshot.Signals.ToDictionary(s => s.Id);
        foreach (var dependency in input.Snapshot.Dependencies)
        {
            var ruleVersion = ChooseRuleVersion(input.ActiveRules, new[] { dependency.Id });
            var result = EvaluateDependency(dependency, signalMap, input.Snapshot);
            results.Add(BuildResult(
                result.Kind,
                result.Title,
                result.Status,
                dependency.Id,
                result.FirstTime,
                result.Evidence,
                result.Rejection,
                result.Uncertainty,
                Array.Empty<StateWindow>(),
                result.Edges,
                result.CandidateEdgeSets,
                new[] { dependency.SourceVersionId },
                ruleVersion,
                new[] { dependency.Id }));
        }

        foreach (var cycles in EnumerateCycles(input.Snapshot.Dependencies).GroupBy(c => string.Join("|", c), StringComparer.Ordinal))
        {
        }

        foreach (var cycleGroup in EnumerateCycles(input.Snapshot.Dependencies)
                     .Select(c => (IReadOnlyList<string>)c)
                     .GroupBy(c => string.Join("|", c), StringComparer.Ordinal)
                     .Select(g => g.OrderBy(x => string.Join(",", x), StringComparer.Ordinal).First()))
        {
            var edgeIds = cycleGroup.ToList();
            var dependencies = edgeIds.Select(id => input.Snapshot.Dependencies.First(d => d.Id == id)).ToList();
            var sourceIds = dependencies.Select(d => d.SourceVersionId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var ruleVersions = input.ActiveRules.Values.Select(r => r.VersionId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fingerprint = Fingerprint(new { kind = "cycle-dependency", edges = edgeIds, sources = sourceIds });
            results.Add(new AnalysisResult(
                StableId("cycle-dependency", string.Join("|", edgeIds), sourceIds, ruleVersions),
                "cycle-dependency",
                "Cyclic dependency requires a minimum contradiction edge set",
                ResultStatus.Candidate,
                string.Join(",", edgeIds),
                null,
                null,
                null,
                null,
                "all legal minimum edge sets are retained until a human selects one",
                Array.Empty<StateWindow>(),
                Array.Empty<EdgeEvidence>(),
                new List<IReadOnlyList<string>> { edgeIds },
                sourceIds.Append("graph-cycle-algorithm").ToArray(),
                AlgorithmVersion,
                fingerprint,
                ruleVersions.FirstOrDefault() ?? "rule-baseline",
                ruleVersions,
                edgeIds,
                sourceIds));
        }

        return new AnalysisRun(input.RunId, input.ParentEventId, input.Ordinal, results
            .GroupBy(r => r.StableId)
            .SelectMany(g => g.Count() == 1 ? g.ToArray() : MergeCycleResults(g.ToArray()))
            .OrderBy(r => r.FirstTime ?? double.MaxValue)
            .ThenBy(r => r.StableId, StringComparer.Ordinal)
            .ToList());
    }

    private static AnalysisResult BuildResult(
        string kind,
        string title,
        ResultStatus status,
        string subjectId,
        double? firstTime,
        string? evidence,
        string? rejection,
        string? uncertainty,
        IReadOnlyList<StateWindow> windows,
        IReadOnlyList<EdgeEvidence> edges,
        IReadOnlyList<IReadOnlyList<string>> candidateEdgeSets,
        IReadOnlyList<string> sources,
        string ruleVersion,
        IReadOnlyList<string> scopes)
    {
        var ruleVersions = new[] { ruleVersion };
        var sourceIds = sources.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return new AnalysisResult(
            StableId(kind, subjectId, sourceIds, ruleVersions),
            kind,
            title,
            status,
            subjectId,
            null,
            firstTime,
            evidence,
            rejection,
            uncertainty,
            windows,
            edges,
            candidateEdgeSets,
            sourceIds,
            AlgorithmVersion,
            Fingerprint(new { kind, subjectId, sourceIds, ruleVersion, scopes }),
            ruleVersion,
            ruleVersions,
            scopes,
            sourceIds);
    }

    private static IEnumerable<AnalysisResult> MergeCycleResults(IReadOnlyList<AnalysisResult> results)
    {
        var edgeSets = results.SelectMany(r => r.CandidateEdgeSets)
            .GroupBy(set => string.Join("|", set), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(set => string.Join(",", set), StringComparer.Ordinal)
            .ToList();
        var first = results[0] with
        {
            CandidateEdgeSets = edgeSets,
            DirectSources = results.SelectMany(r => r.DirectSources).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            SourceVersions = results.SelectMany(r => r.SourceVersions).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            RuleVersions = results.SelectMany(r => r.RuleVersions).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ScopeSignals = results.SelectMany(r => r.ScopeSignals).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList()
        };
        yield return first;
    }

    private static Dictionary<string, JsonElement> BuildEffectiveRules(CaseSnapshot snapshot, JsonElement? overrides)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (snapshot.BaselineRules is { } baseline && baseline.TryGetProperty("signals", out var baselineSignals))
            foreach (var rule in baselineSignals.EnumerateArray())
                map[rule.GetProperty("signalId").GetString()!] = rule;

        if (overrides is { } root && root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("signals", out var revisedSignals))
                foreach (var rule in revisedSignals.EnumerateArray())
                    map[rule.GetProperty("signalId").GetString()!] = rule;
            if (root.TryGetProperty("dependencies", out var revisedDependencies))
                foreach (var rule in revisedDependencies.EnumerateArray())
                    map[rule.GetProperty("dependencyId").GetString()!] = rule;
        }

        return map;
    }

    private static string ChooseRuleVersion(IReadOnlyDictionary<string, RuleVersion> active, IReadOnlyList<string> scopeIds)
    {
        var matching = active.Values
            .Where(rule =>
            {
                if (!rule.Body.TryGetProperty("scope", out var scope))
                    return true;
                if (scope.ValueKind == JsonValueKind.String)
                    return scopeIds.Contains(scope.GetString());
                return scope.ValueKind == JsonValueKind.Array &&
                       scope.EnumerateArray().Any(x => scopeIds.Contains(x.GetString()));
            })
            .OrderByDescending(rule => rule.EventOrdinal)
            .FirstOrDefault();
        return matching?.VersionId ?? "rule-baseline";
    }

    private sealed record Excursion(string ExpectedState, double? StartTime, double? EndTime, double Duration, string StartSampleId, string EndSampleId);

    private sealed record ClockGapEvidence(double Time, double GapSeconds, double LimitSeconds, string BeforeSampleId, string AfterSampleId);

    private sealed record SignalEvaluation(
        IReadOnlyList<StateWindow> Windows,
        IReadOnlyList<EdgeEvidence> Edges,
        IReadOnlyList<Excursion> Excursions,
        IReadOnlyList<ClockGapEvidence> ClockGaps);

    private static SignalEvaluation EvaluateSignal(
        SignalSpec signal,
        IReadOnlyList<ClockSpec> clocks,
        IReadOnlyDictionary<string, JsonElement> rules)
    {
        var rise = NumberRule(signal, rules, "riseThreshold") ?? signal.RiseThreshold.Numeric;
        var fall = NumberRule(signal, rules, "fallThreshold") ?? signal.FallThreshold.Numeric;
        var hysteresis = NumberRule(signal, rules, "hysteresis") ?? signal.Hysteresis.Numeric ?? 0;
        var minimumStable = NumberRule(signal, rules, "minimumStable") ?? signal.MinimumStable.Numeric ?? 0;
        if (!hysteresis.Equals(0) && signal.Hysteresis.Numeric is null)
        {
        }

        var windows = new List<StateWindow>();
        var edges = new List<EdgeEvidence>();
        var excursions = new List<Excursion>();
        var gaps = new List<ClockGapEvidence>();
        var state = "unknown";
        double? windowStart = null;
        var windowSamples = new List<string>();
        string? lastState = null;
        RawSample? previous = null;

        foreach (var sample in signal.Samples)
        {
            var t = sample.Time.Numeric;
            var v = sample.Value.Numeric;
            if (previous is not null && signal.ClockId is not null)
            {
                var clock = clocks.FirstOrDefault(c => c.Id == signal.ClockId);
                if (clock is not null && previous.Time.Numeric is { } pt && t is { } ct)
                {
                    var period = clock.ExpectedPeriod.Numeric ?? 0;
                    var limit = clock.GapLimit.Numeric ?? period * 1.5;
                    if (period > 0 && ct - pt > limit)
                    {
                        gaps.Add(new ClockGapEvidence((pt + ct) / 2.0, ct - pt, limit, previous.Id, sample.Id));
                        if (state != "unknown")
                        {
                            CloseWindow();
                            state = "unknown";
                            windowStart = pt;
                            windowSamples = new List<string> { previous.Id, sample.Id };
                            CloseWindowAt(ct, false, "clock gap; state not observable");
                            windowStart = null;
                        }
                    }
                }
            }

            var nextState = v is null
                ? "unknown"
                : state switch
                {
                    "high" when v >= fall - hysteresis => "high",
                    "low" when v < rise + hysteresis => "low",
                    _ when v >= rise => "high",
                    _ when v < fall => "low",
                    _ => "hysteresis-band"
                };

            if (v is not null && previous?.Value.Numeric is { } oldValue && t is { } newTime && previous.Time.Numeric is { } oldTime)
            {
                var nextEdge = nextState == "high" && state != "high" ? "rise" :
                    nextState == "low" && state != "low" ? "fall" : null;
                var threshold = nextEdge == "rise" ? rise : nextEdge == "fall" ? fall : (double?)null;
                if (nextEdge is not null && threshold is not null)
                {
                    var edgeTime = InterpolateCrossing(oldTime, oldValue, newTime, sample.Value.Numeric!.Value, threshold.Value);
                    var uncertainty = TimeUncertainty(signal, previous!, sample);
                    edges.Add(new EdgeEvidence(nextEdge, edgeTime, uncertainty, false, sample.Id, "evidence"));
                }
            }

            if (nextState != state && state != "unknown" && nextState is "low" or "hysteresis-band" or "unknown")
            {
                var start = windowStart ?? previous?.Time.Numeric;
                var duration = (t ?? start ?? 0) - (start ?? 0);
                if (state == "high" && (nextState == "low" || nextState == "unknown") && duration < minimumStable)
                {
                }
                CloseWindow();
                var badStart = previous?.Time.Numeric ?? t;
                var badEnd = t;
                excursions.Add(new Excursion("high", badStart, badEnd, (badEnd ?? badStart ?? 0) - (badStart ?? badEnd ?? 0), previous?.Id ?? sample.Id, sample.Id));
            }

            if (nextState != state)
            {
                if (windowStart is not null)
                    CloseWindowAt(t, IsStableState(state), transitionOnly: true);
                state = nextState;
                windowStart = t;
                windowSamples = new List<string> { sample.Id };
            }
            else
            {
                windowSamples.Add(sample.Id);
            }

            lastState = state;
            previous = sample;
        }

        if (windowStart is not null && previous is not null)
            CloseWindowAt(previous.Time.Numeric, IsStableState(state));

        return new SignalEvaluation(windows, edges, excursions, gaps);

        void CloseWindow() => CloseWindowAt(previous?.Time.Numeric, IsStableState(state));

        void CloseWindowAt(double? end, bool stable, string? reason = null, bool transitionOnly = false)
        {
            if (windowStart is null)
                return;
            var duration = (end ?? windowStart) - windowStart;
            windows.Add(new StateWindow(
                state,
                windowStart,
                end,
                stable && duration >= minimumStable,
                reason ?? (stable && duration >= minimumStable ? "meets minimum stable time" : transitionOnly ? "state transition" : "short or unknown window"),
                windowSamples.ToArray()));
            windowStart = null;
            windowSamples = new List<string>();
        }
    }
    private static SignalEvaluation EvaluateSignal(
        SignalSpec signal,
        IReadOnlyList<ClockSpec> clocks,
        IReadOnlyDictionary<string, JsonElement> rules)
    {
        var rise = NumberRule(signal, rules, "riseThreshold") ?? signal.RiseThreshold.Numeric ?? 0;
        var fall = NumberRule(signal, rules, "fallThreshold") ?? signal.FallThreshold.Numeric ?? 0;
        var hysteresis = NumberRule(signal, rules, "hysteresis") ?? signal.Hysteresis.Numeric ?? 0;
        var minimumStable = NumberRule(signal, rules, "minimumStable") ?? signal.MinimumStable.Numeric ?? 0;

        var windows = new List<StateWindow>();
        var edges = new List<EdgeEvidence>();
        var excursions = new List<Excursion>();
        var gaps = new List<ClockGapEvidence>();
        var state = "unknown";
        double? windowStart = null;
        var windowSamples = new List<string>();
        RawSample? previous = null;

        foreach (var sample in signal.Samples)
        {
            var currentTime = sample.Time.Numeric;
            var currentValue = sample.Value.Numeric;

            if (previous is not null && signal.ClockId is not null)
            {
                var clock = clocks.FirstOrDefault(c => c.Id == signal.ClockId);
                if (clock is not null && previous.Time.Numeric is { } previousTime && currentTime is { } thisTime)
                {
                    var period = clock.ExpectedPeriod.Numeric ?? 0;
                    var limit = clock.GapLimit.Numeric ?? period * 1.5;
                    if (period > 0 && thisTime - previousTime > limit)
                        gaps.Add(new ClockGapEvidence((previousTime + thisTime) / 2.0, thisTime - previousTime, limit, previous.Id, sample.Id));
                }
            }

            var nextState = NextState(state, currentValue, rise, fall, hysteresis);

            if (previous is not null && previous.Value.Numeric is { } oldValue &&
                currentValue is { } newValue && previous.Time.Numeric is { } oldTime &&
                currentTime is { } newTime)
            {
                string? edge = nextState == "high" && state != "high" ? "rise"
                    : nextState == "low" && state != "low" ? "fall"
                    : null;
                var threshold = edge == "rise" ? rise : edge == "fall" ? fall : (double?)null;
                if (edge is not null && threshold is not null)
                {
                    var crossing = InterpolateCrossing(oldTime, oldValue, newTime, newValue, threshold.Value);
                    var uncertainty = TimeUncertainty(signal, previous, sample);
                    edges.Add(new EdgeEvidence(edge, crossing, uncertainty, false, sample.Id, "evidence"));
                }
            }

            if (nextState != state)
            {
                CloseWindow(currentTime, previous?.Id);
                state = nextState;
                windowStart = currentTime;
                windowSamples = new List<string> { sample.Id };
            }
            else
            {
                windowSamples.Add(sample.Id);
            }

            previous = sample;
        }

        CloseWindow(previous?.Time.Numeric, previous?.Id);

        foreach (var window in windows.Where(w => w.State == "high"))
        {
            var stable = window.Stable;
            if (window.End is null || window.Start is null)
                continue;
            var duration = window.End.Value - window.Start.Value;
            excursions.Add(new Excursion(
                "high",
                window.Start,
                window.End,
                duration,
                window.SampleIds.FirstOrDefault() ?? string.Empty,
                window.SampleIds.LastOrDefault() ?? string.Empty)
            {
                Stable = stable
            });
        }

        return new SignalEvaluation(windows, edges, excursions, gaps);

        void CloseWindow(double? endTime, string? endSampleId)
        {
            if (windowStart is null)
                return;
            var end = endTime ?? windowStart;
            var duration = end - windowStart.Value;
            var stable = state != "unknown" && duration >= minimumStable;
            var reason = state == "unknown"
                ? "unknown because value or clock observation is unavailable"
                : stable
                    ? "meets minimum stable time"
                    : $"duration {duration:R} s is below minimum stable {minimumStable:R} s";
            windows.Add(new StateWindow(state, windowStart, end, stable, reason, windowSamples.ToArray()));
            windowStart = null;
            windowSamples = new List<string>();
        }
    }

    private static string NextState(string state, double? value, double rise, double fall, double hysteresis)
    {
        if (value is null)
            return "unknown";
        if (state == "high")
            return value.Value >= fall - hysteresis ? "high" : "low";
        if (state == "low")
            return value.Value < rise + hysteresis ? "low" : "high";
        if (value.Value >= rise)
            return "high";
        if (value.Value < fall)
            return "low";
        return "hysteresis-band";
    }

    private static double? NumberRule(SignalSpec signal, IReadOnlyDictionary<string, JsonElement> rules, string property)
    {
        if (rules.TryGetValue(signal.Id, out var rule) && rule.TryGetProperty(property, out var value))
        {
            var parsed = TypedValue.Parse(value, property.Contains("Stable") ? "duration" : "voltage");
            return parsed.Numeric;
        }
        return null;
    }

    private static double InterpolateCrossing(double t0, double v0, double t1, double v1, double threshold)
    {
        if (Math.Abs(v1 - v0) < double.Epsilon)
            return t0;
        var fraction = (threshold - v0) / (v1 - v0);
        return Math.Clamp(fraction, 0, 1) * (t1 - t0) + t0;
    }

    private static double TimeUncertainty(SignalSpec signal, RawSample first, RawSample second)
    {
        var baseUncertainty = Math.Abs(second.Time.Numeric.GetValueOrDefault() - first.Time.Numeric.GetValueOrDefault()) / 2.0;
        if (signal.MeasurementError?.Numeric is { } voltageError)
        {
            var firstValue = first.Value.Numeric ?? 0;
            var secondValue = second.Value.Numeric ?? 1;
            var slope = Math.Abs(secondValue - firstValue);
            if (slope > double.Epsilon)
                baseUncertainty = Math.Max(baseUncertainty, voltageError / slope);
        }
        return baseUncertainty;
    }
