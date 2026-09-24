using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PowerSequencer.Domain;

/// <summary>A quantity in its original representation: value + unit, nothing lost.</summary>
public sealed record Quantity(double Value, string Unit)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value} {Unit}");
}

public enum UnitKind { Voltage, Time }

public static class Units
{
    private static readonly Dictionary<string, double> Voltage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"] = 1.0, ["mV"] = 1e-3, ["uV"] = 1e-6, ["µV"] = 1e-6
    };

    private static readonly Dictionary<string, double> Time = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s"] = 1.0, ["ms"] = 1e-3, ["us"] = 1e-6, ["µs"] = 1e-6, ["ns"] = 1e-9
    };

    /// <summary>Normalize to canonical units (Volt / second). Unconvertible values stay raw.</summary>
    public static bool TryNormalize(Quantity q, UnitKind kind, out double canonical, out string? error)
    {
        var table = kind == UnitKind.Voltage ? Voltage : Time;
        if (q.Unit is null || !table.TryGetValue(q.Unit, out var factor))
        {
            canonical = 0;
            error = $"unknown-{kind.ToString().ToLowerInvariant()}-unit:{q.Unit ?? "<null>"}";
            return false;
        }
        canonical = q.Value * factor;
        error = null;
        return true;
    }
}

public static class Hashing
{
    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ShortId(string prefix, string content, int len = 16) =>
        $"{prefix}-{Sha256(content)[..len]}";
}

/// <summary>A raw sample point exactly as submitted (original units preserved).</summary>
public sealed record RawSample(Quantity Time, Quantity Value);

/// <summary>One imported waveform / digital signal stream (immutable once imported).</summary>
public sealed record WaveformData(
    string Rail,
    string SignalKind, // "voltage" | "enable" | "good"
    List<RawSample> Samples,
    Quantity SampleClock,
    Quantity ClockJitter,
    Quantity MeasurementError);

public sealed record RailConstraint(
    string Rail,
    Quantity RiseThreshold,
    Quantity FallThreshold,
    Quantity Hysteresis,
    Quantity MinStableTime);

public sealed record DependencyEdge(string From, string To, string Kind, Quantity? MaxDelay);

public sealed record ConstraintSet(List<RailConstraint> Rails, List<DependencyEdge> Edges)
{
    public RailConstraint? ForRail(string rail) =>
        Rails.FirstOrDefault(r => string.Equals(r.Rail, rail, StringComparison.OrdinalIgnoreCase));
}

// ---------- Analysis outputs (derived values, always carry provenance) ----------

public enum RailState { Low, High, Unknown }

public sealed record StateWindow(
    string Rail, RailState State, double TStart, double TEnd, bool Certain, string Reason);

public static class ExcursionClass
{
    public const string Glitch = "glitch";
    public const string Violation = "violation";
    public const string Ambiguous = "ambiguous";
    public const string Unknown = "unknown";
}

public sealed record Excursion(
    string Rail,
    string Direction, // "dip" (from High) | "spike" (from Low)
    double TStart, double TEnd, double Duration,
    string Classification,
    List<string> Candidates,
    string Reason);

public sealed record Diagnostic(string Code, string Message, string? Rail, double? At);

public sealed record SourceRef(string Kind, string Id, int Version)
{
    public override string ToString() => $"{Kind}:{Id}@v{Version}";
}

public sealed record RailAnalysisResult(
    string Rail,
    string AlgoVersion,
    string ParamFingerprint,
    List<SourceRef> Sources,
    List<StateWindow> Windows,
    List<Excursion> Excursions,
    List<Diagnostic> Diagnostics);

/// <summary>A group of events that arrived simultaneously within timing tolerance.</summary>
public sealed record SequenceStep(List<string> Rails, double Time, string Basis);

public sealed record ShutdownMismatch(int Index, List<string> Expected, List<string> Actual, double At, string Reason);

public sealed record SequenceAnalysisResult(
    string AlgoVersion,
    string ParamFingerprint,
    List<SourceRef> Sources,
    List<SequenceStep> Startup,
    List<SequenceStep> Shutdown,
    List<List<string>> StartupCandidates, // all legal orderings when simultaneous arrival
    List<ShutdownMismatch> Mismatches,
    List<Diagnostic> Diagnostics);

public sealed record EdgeVerdict(DependencyEdge Edge, string Verdict, double? At, string Reason);
// Verdict: "satisfied" | "violated" | "ambiguous" | "unknown"

public sealed record CycleConflict(List<List<DependencyEdge>> MinimalEdgeSets, string Reason);

public sealed record GraphAnalysisResult(
    string AlgoVersion,
    string ParamFingerprint,
    List<SourceRef> Sources,
    List<EdgeVerdict> Verdicts,
    CycleConflict? Cycles,
    List<Diagnostic> Diagnostics);

public sealed record FirstViolation(
    double At, string Kind, string Description,
    string EvidenceResultId, List<SourceRef> Sources);

public sealed record FirstViolationResult(
    string AlgoVersion,
    string ParamFingerprint,
    List<SourceRef> Sources,
    FirstViolation? Violation,
    List<string> Considered);
