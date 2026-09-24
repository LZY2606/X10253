namespace PowerSequencing.Core;

/// <summary>Provenance pointer: which submitted material line produced an item.</summary>
public sealed record Origin(string CommitId, int Line, string RawLine, string Kind);

public sealed record RailState(
    RailDef Def,
    Origin Origin,
    string ParamFingerprint,
    string? LastChangedByCommit);

public sealed record SampleState(
    SamplePoint Point,
    Origin Origin,
    string Fingerprint);

public sealed record DepState(
    DependencyEdge Edge,
    Origin Origin);

public sealed record GapState(GapAnnotation Gap, Origin Origin);

/// <summary>
/// The full material view at a commit: raw inputs are never overwritten, this
/// is a derived projection rebuilt by replaying commits.  Every projected item
/// remembers its direct source commit and source line.
/// </summary>
public sealed class MaterializedState
{
    public required string CaseId { get; init; }
    public required string CommitId { get; init; }
    public RawTime ClockPeriod { get; init; } = null!;
    public Dictionary<string, RailState> Rails { get; } = new();
    public Dictionary<string, SignalDef> Signals { get; } = new();
    public List<EdgeEvent> Edges { get; } = [];
    public Dictionary<string, DepState> Deps { get; } = new();
    public Dictionary<int, SampleState> Samples { get; } = new();
    public List<PhaseWindow> Phases { get; } = [];
    public List<GapState> Gaps { get; } = new();

    public string ParameterFingerprint(string rail)
    {
        var r = Rails[rail];
        return StableId.Of("params", string.Join("\u0001",
            rail, r.Def.RiseThreshold.Raw, r.Def.FallThreshold.Raw,
            r.Def.MinStable.Raw, r.Def.GlitchTolerance.Raw));
    }

    public string ClockFingerprint => StableId.Of("clock", ClockPeriod.Raw);
}
