namespace PowerSeqEvidenceRoom.Import;

public enum DiagSeverity { Error, Warning }

public sealed class ParseDiagnostic
{
    public required DiagSeverity Severity { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string Coord { get; init; } = "";
    public override string ToString() => $"{Coord}: {Severity} {Code}: {Message}";
}

public sealed class ParseResult
{
    public Domain.InputDocument? Document { get; init; }
    public required List<ParseDiagnostic> Diagnostics { get; init; }
    public bool HasFatalErrors => Diagnostics.Any(d => d.Severity == DiagSeverity.Error);
}
