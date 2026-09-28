using System.Globalization;
using System.Text.Json;

namespace PowerEvidence.Domain;

public enum SignalRole { Voltage, Enable, Good }
public enum ConstraintPhase { Startup, Shutdown, Both }
public enum ResultStatus { Evidence, Candidate, Accepted, Rejected, Unknown, Conflict, Stale }
public enum DecisionKind { Accept, Reject, AcceptCycleEdgeSet }

public sealed record TypedValue(
    string Original,
    string? Unit,
    double? Numeric,
    bool Converted,
    string? Basis,
    string? Diagnostic)
{
    public string Coordinate => Numeric.HasValue
        ? Numeric.Value.ToString("R", CultureInfo.InvariantCulture) + " " + (Basis ?? Unit ?? "")
        : Original + (Unit is null ? string.Empty : " " + Unit);

    public static TypedValue Parse(JsonElement element, string expectedDimension)
    {
        var raw = element.GetRawText();
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("original", out var originalProp))
        {
            var text = originalProp.GetString() ?? raw;
            var unit = element.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;
            var diagnostic = element.TryGetProperty("diagnostic", out var d) ? d.GetString() : null;
            if (element.TryGetProperty("numeric", out var n) && n.ValueKind == JsonValueKind.Number)
            {
                return new TypedValue(text, unit, n.GetDouble(), true, expectedDimension, diagnostic);
            }

            return new TypedValue(text, unit, null, false, expectedDimension, diagnostic ?? "unconverted value retained");
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            var unit = expectedDimension switch
            {
                "time" => "s",
                "voltage" => "V",
                _ => null
            };
            return new TypedValue(raw, unit, element.GetDouble(), true, expectedDimension, null);
        }

        if (element.ValueKind != JsonValueKind.String)
            return new TypedValue(raw, null, null, false, expectedDimension, "unsupported value kind; value retained at source");

        var source = element.GetString() ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(
            source.Trim(),
            @"^([+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?)\s*([^\s]+)?$");
        if (!match.Success)
            return new TypedValue(source, null, null, false, expectedDimension, "numeric body not recognized; value retained at source");

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return new TypedValue(source, match.Groups[2].Value, null, false, expectedDimension, "numeric body not parsed; value retained at source");

        var sourceUnit = string.IsNullOrWhiteSpace(match.Groups[2].Value) ? null : match.Groups[2].Value;
        var (normalized, normalizedUnit, diagnostic) = NormalizeUnit(number, sourceUnit, expectedDimension);
        return new TypedValue(source, sourceUnit, normalized, normalizedUnit is not null, normalizedUnit ?? expectedDimension, diagnostic);
    }

    private static (double?, string?, string?) NormalizeUnit(double value, string? unit, string dimension)
    {
        if (dimension == "time")
        {
            return unit switch
            {
                "s" => (value, "s", null),
                "ms" => (value / 1000.0, "s", null),
                "us" or "µs" => (value / 1_000_000.0, "s", null),
                "ns" => (value / 1_000_000_000.0, "s", null),
                null => (value, "s", "unit omitted; SI second assumed"),
                _ => (null, null, $"unknown time unit '{unit}'; raw value retained")
            };
        }

        if (dimension == "voltage")
        {
            return unit switch
            {
                "V" => (value, "V", null),
                "mV" => (value / 1000.0, "V", null),
                "uV" or "µV" => (value / 1_000_000.0, "V", null),
                null => (value, "V", "unit omitted; volt assumed"),
                _ => (null, null, $"unknown voltage unit '{unit}'; raw value retained")
            };
        }

        if (dimension == "duration")
        {
            return unit switch
            {
                "s" => (value, "s", null),
                "ms" => (value / 1000.0, "s", null),
                "us" or "µs" => (value / 1_000_000.0, "s", null),
                "ns" => (value / 1_000_000_000.0, "s", null),
                null => (value, "s", "unit omitted; SI second assumed"),
                _ => (null, null, $"unknown duration unit '{unit}'; raw value retained")
            };
        }

        return (value, unit, null);
    }
}

public sealed record RawSample(string Id, TypedValue Time, TypedValue Value, string? MeasurementError);

public sealed record SignalSpec(
    string Id,
    string RailId,
    string Label,
    SignalRole Role,
    TypedValue RiseThreshold,
    TypedValue FallThreshold,
    TypedValue Hysteresis,
    TypedValue MinimumStable,
    TypedValue? MeasurementError,
    IReadOnlyList<RawSample> Samples,
    string SourceVersionId,
    string? ClockId);

public sealed record DependencySpec(
    string Id,
    string FromRail,
    string FromSignal,
    string FromEdge,
    string ToRail,
    string ToSignal,
    string ToEdge,
    ConstraintPhase Phase,
    bool AllowSimultaneous,
    TypedValue MinimumGap,
    string SourceVersionId);

public sealed record ClockSpec(
    string Id,
    TypedValue ExpectedPeriod,
    TypedValue GapLimit,
    string SourceVersionId);

public sealed record RailSpec(string Id, string Name, string SourceVersionId);

public sealed record StateWindow(
    string State,
    double? Start,
    double? End,
    bool Stable,
    string Reason,
    IReadOnlyList<string> SampleIds);

public sealed record EdgeEvidence(
    string Edge,
    double? Time,
    double? Uncertainty,
    bool Simultaneous,
    string SampleId,
    string Status);

public sealed record AnalysisResult(
    string StableId,
    string Kind,
    string Title,
    ResultStatus Status,
    string SubjectId,
    string? Phase,
    double? FirstTime,
    string? FirstEvidence,
    string? RejectionReason,
    string? Uncertainty,
    IReadOnlyList<StateWindow> Windows,
    IReadOnlyList<EdgeEvidence> Edges,
    IReadOnlyList<IReadOnlyList<string>> CandidateEdgeSets,
    IReadOnlyList<string> DirectSources,
    string AlgorithmVersion,
    string ParameterFingerprint,
    string RuleVersion,
    IReadOnlyList<string> RuleVersions,
    IReadOnlyList<string> ScopeSignals,
    IReadOnlyList<string> SourceVersions);

public sealed record AnalysisRun(
    string RunId,
    string ParentEventId,
    int AtOrdinal,
    IReadOnlyList<AnalysisResult> Results);

public sealed record DecisionRecord(
    string EventId,
    int Ordinal,
    string StableResultId,
    DecisionKind Kind,
    string Note,
    IReadOnlyList<string>? EdgeSet,
    string? SupersedesEventId);
