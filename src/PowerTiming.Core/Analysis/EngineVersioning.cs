using System.Text;
using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Analysis;

/// <summary>
/// Stable content identities used to decide exactly which derived results expire.
/// </summary>
public static class EngineVersioning
{
    public static string ClockVersion(ClockModel clock)
    {
        var builder = new StringBuilder();
        builder.Append(clock.NominalPeriod.Raw).Append('|');
        builder.Append(clock.Start.Raw).Append('|');
        builder.Append(clock.TimeMeasurementError?.Raw).Append('|');
        builder.Append(clock.VoltageMeasurementError?.Raw).Append('|');
        foreach (var gap in clock.Gaps)
        {
            builder.Append(gap.Start.Raw).Append(',').Append(gap.End.Raw).Append(',').Append(gap.Reason).Append(';');
        }

        return Hashing.StableId("clk", builder.ToString(), 16);
    }

    public static string RailVersion(RailInput rail)
    {
        var builder = new StringBuilder();
        builder.Append(rail.RailId).Append('|')
            .Append(rail.Nominal.Raw).Append('|')
            .Append(rail.RiseThreshold.Raw).Append('|')
            .Append(rail.FallThreshold.Raw).Append('|')
            .Append(rail.HysteresisBand?.Raw).Append('|')
            .Append(rail.MinStable.Raw).Append('|');
        foreach (var sample in rail.Samples)
        {
            builder.Append(sample.Time.Raw).Append('@').Append(sample.Voltage.Raw)
                .Append('[').Append(sample.MeasurementError?.Raw).Append(']')
                .Append('{').Append(string.Join(",", sample.Flags)).Append('};');
        }

        foreach (var signal in rail.Signals)
        {
            builder.Append(signal.Signal).Append(':').Append(signal.Kind).Append('@').Append(signal.At.Raw).Append(';');
        }

        return Hashing.StableId("rv", builder.ToString(), 16);
    }

    public static string EdgeVersion(DependencyEdge edge)
    {
        var content = string.Join('|',
            edge.EdgeId,
            edge.FromRail,
            edge.ToRail,
            edge.MinDelay.Raw,
            edge.SimultaneousTolerance.Raw,
            edge.RuleVersion,
            string.Join(">", edge.StartupOrder),
            string.Join(">", edge.ShutdownOrder));
        return Hashing.StableId("ev", content, 16);
    }

    public static string RailResultId(string railVersion, string clockVersion)
        => Hashing.StableId("rw", $"{EngineInfo.AlgorithmVersion}|{railVersion}|{clockVersion}", 18);

    public static string EdgeResultId(string edgeVersion, string fromRailVersion, string toRailVersion, string clockVersion, string phase, bool waived)
        => Hashing.StableId("ec", $"{EngineInfo.AlgorithmVersion}|{edgeVersion}|{fromRailVersion}|{toRailVersion}|{clockVersion}|{phase}|waived={waived}", 18);

    public static string CycleResultId(IEnumerable<string> activeEdgeVersions, IEnumerable<string> waivedEdgeIds)
        => Hashing.StableId("cy", $"{EngineInfo.AlgorithmVersion}|{string.Join(",", activeEdgeVersions.OrderBy(x => x, StringComparer.Ordinal))}|{string.Join(",", waivedEdgeIds.OrderBy(x => x, StringComparer.Ordinal))}", 18);
}

public static class JsonUtil
{
    public static System.Text.Json.JsonElement Element(object value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, EvidenceJsonOptions.Default);
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }
}

public static class EvidenceJsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
