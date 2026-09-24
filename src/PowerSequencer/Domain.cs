using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerSequencer;

/// <summary>Virtual clock so tests and replays can pin time.</summary>
public interface IClock
{
    DateTimeOffset Now();
}

public sealed class FixedClock : IClock
{
    private DateTimeOffset _now;
    public FixedClock(DateTimeOffset now) => _now = now;
    public DateTimeOffset Now() => _now;
    public void Advance(TimeSpan dt) => _now = _now.Add(dt);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}

/// <summary>Stable, content-addressed identity helpers.</summary>
public static class Identity
{
    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>Canonical JSON: object keys sorted, numbers invariant. Used for fingerprints.</summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s))
                    sb.Append(JsonSerializer.Serialize(s));
                else if (v.TryGetValue<bool>(out var b))
                    sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<double>(out var d))
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                else if (v.TryGetValue<long>(out var l))
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(v.ToJsonString());
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
        }
    }

    public static string Fingerprint(JsonNode? node) => Hash(Canonical(node));
}

/// <summary>Raw sample point; the original textual representation is always preserved.</summary>
public sealed record SamplePoint(double T, double V, string RawT, string RawV);

public sealed record RailSeries(
    string Rail,
    string VoltageUnit,
    string TimeUnit,
    List<SamplePoint> Samples,
    List<string> Diagnostics);

public sealed record DigitalSeries(
    string Name,
    string Kind, // "enable" | "good"
    string TimeUnit,
    List<SamplePoint> Samples,
    List<string> Diagnostics);

public sealed record ClockSpec(double PeriodNs, double JitterNs, double ErrorV, string Raw);

public sealed record RailThreshold(
    string Rail,
    double RiseV,
    double FallV,
    double HysteresisV,
    double MinStableNs);

public sealed record DependencyEdge(string From, string To, string Kind, double? MaxDelayNs);
// Kind: "rise-after"  => To may rise only after From is Good (startup order)
// Shutdown is checked as the exact reverse of every rise-after edge.

public static class Algo
{
    public const string Window = "window-v1";
    public const string Dependency = "dep-v1";
    public const string Cycle = "cycle-v1";
}

public enum RailState
{
    Off,
    Good,
    Unknown
}

public sealed record StateWindow(RailState State, double TStart, double TEnd, string Reason);

/// <summary>Machine-readable judgement event emitted by analysis.</summary>
public sealed record AnalysisEvent(
    string Type,       // transition | glitch | violation | unknown-region
    string Rail,
    double TStart,
    double DurationNs,
    string Direction,  // rise | fall | none
    JsonObject Basis); // threshold version, values, min stable, hysteresis, clock

public sealed record RailAnalysis(
    string Rail,
    List<StateWindow> Windows,
    List<AnalysisEvent> Events);

public enum EdgeStatus
{
    Satisfied,
    Violated,
    Unknown
}

public sealed record EdgeVerdict(
    DependencyEdge Edge,
    EdgeStatus Status,
    string? Evidence,
    double? TFrom,
    double? TTo,
    List<string> Candidates);

public sealed record DependencyReport(
    List<EdgeVerdict> Edges,
    List<List<DependencyEdge>> MinimalContradictionSets, // all minimum feedback edge sets
    List<List<string>> Cycles,
    EdgeVerdict? FirstViolation);
