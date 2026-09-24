namespace PowerSequencing.Core;

/// <summary>A single immutable, append-only revision statement.</summary>
public abstract record PatchOp(string Kind, int SourceLine, string RawLine);

public sealed record AddDepOp(string From, string To, RawTime? MinGap, RawTime MaxGap, int Line, string Raw)
    : PatchOp("add_dep", Line, Raw);

public sealed record RemoveDepOp(string From, string To, int Line, string Raw)
    : PatchOp("remove_dep", Line, Raw);

public sealed record UpdateRailOp(string Rail, RawQuantity? Rise, RawQuantity? Fall,
    RawTime? MinStable, RawTime? GlitchMax, int Line, string Raw) : PatchOp("update_rail", Line, Raw);

public sealed record ReplaceSampleOp(int Index, string Rail, RawQuantity Value, RawQuantity? Unc, int Line, string Raw)
    : PatchOp("replace_sample", Line, Raw);

public sealed record AddSampleOp(SamplePoint Sample, int Line, string Raw)
    : PatchOp("add_sample", Line, Raw);

public sealed record AddGapOp(GapAnnotation Gap, int Line, string Raw)
    : PatchOp("gap", Line, Raw);

public sealed record PatchDoc(
    string? Message,
    IReadOnlyList<PatchOp> Ops,
    IReadOnlyList<ImportDiagnostic> Errors,
    string RawText)
{
    public bool IsValid => Errors.Count == 0;
}
