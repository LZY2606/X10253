namespace PowerSequencing.Core;

/// <summary>A time token such as <c>40ms</c>, original spelling retained.</summary>
public sealed record RawTime(string Raw, string Unit, double? TotalMs, string? Error)
{
    public bool IsValid => TotalMs is not null && Error is null;
}

public static class TimeParser
{
    private static readonly (string Unit, double Ms)[] Units =
        [("ns", 1e-6), ("us", 1e-3), ("\u00b5s", 1e-3), ("ms", 1.0), ("s", 1000.0)];

    public static RawTime Parse(string? token)
    {
        var raw = token?.Trim() ?? string.Empty;
        if (raw.Length == 0) return new RawTime(raw, string.Empty, null, "empty time");
        var u = Units.FirstOrDefault(x => raw.EndsWith(x.Unit, StringComparison.OrdinalIgnoreCase));
        if (u.Unit is null)
            return new RawTime(raw, string.Empty, null, $"unrecognised time unit in '{raw}' (accepted: ns, us, ms, s)");
        var num = raw[..^u.Unit.Length].Trim();
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return new RawTime(raw, u.Unit, null, $"time numeric part '{num}' is not a real number");
        return new RawTime(raw, u.Unit, v * u.Ms, null);
    }
}

public sealed record RailDef(
    string Name,
    string? Label,
    RawQuantity RiseThreshold,
    RawQuantity FallThreshold,
    RawTime MinStable,
    RawTime GlitchTolerance,
    int SourceLine,
    string RawLine);

public sealed record SignalDef(string Name, string? Label, bool ActiveHigh, int SourceLine, string RawLine);

public sealed record EdgeEvent(string Signal, bool Up, RawTime At, int SourceLine, string RawLine);

public sealed record DependencyEdge(
    string From,
    string To,
    RawTime? MinGap,
    RawTime MaxGap,
    int SourceLine,
    string RawLine)
{
    public string Slot => $"{From}->{To}";
}

public sealed record RailReading(string Rail, RawQuantity Value, RawQuantity? Uncertainty, int SourceLine, string RawToken);

public sealed record LogicReading(string Signal, bool Level, string RawToken, int SourceLine);

public sealed record SamplePoint(
    int Index,
    RawTime At,
    IReadOnlyList<RailReading> Voltages,
    IReadOnlyList<LogicReading> Logic,
    int SourceLine,
    string RawLine);

public sealed record PhaseWindow(string Name, RawTime From, RawTime To, int SourceLine, string RawLine);

public sealed record GapAnnotation(RawTime From, RawTime To, string? Reason, int SourceLine, string RawLine);

public sealed record ImportDiagnostic(int Line, string Code, string Message, string RawLine);

public sealed record CaseDoc(
    string? Name,
    RawTime? ClockPeriod,
    IReadOnlyList<RailDef> Rails,
    IReadOnlyList<SignalDef> Signals,
    IReadOnlyList<EdgeEvent> Edges,
    IReadOnlyList<DependencyEdge> Deps,
    IReadOnlyList<SamplePoint> Samples,
    IReadOnlyList<PhaseWindow> Phases,
    IReadOnlyList<GapAnnotation> Gaps,
    IReadOnlyList<ImportDiagnostic> Errors,
    IReadOnlyList<ImportDiagnostic> Warnings,
    string RawText)
{
    public bool IsValid => Errors.Count == 0;
    public string Fingerprint => StableId.Of("doc", RawText.Replace("\r\n", "\n"));
}
