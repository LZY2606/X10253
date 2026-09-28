using System.Text.Json.Nodes;

namespace PowerEvidenceRoom.Domain;

public sealed record ImportedPackage(
    JsonObject Original,
    JsonObject Current,
    string PackageFingerprint,
    string? ParentPackageId,
    IReadOnlyList<SourceDocument> Sources,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ForkThresholdCommand(
    string Actor,
    string Rationale,
    string TargetRail,
    double? RisingEnterVolts,
    double? FallingExitVolts,
    double? RisingHysteresisVolts,
    long? MinStableNs);

public sealed record ReviseSourceCommand(
    string Actor,
    string Rationale,
    string SourceKind,
    string Subject,
    JsonNode Replacement);

public sealed record DecisionCommand(
    string Actor,
    string CandidateId,
    bool Accept,
    string Rationale);

public sealed record ReverseDecisionCommand(
    string Actor,
    string CandidateId,
    string Rationale);

public sealed class ProjectState
{
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public string? CurrentPackageId { get; set; }
    public string? CurrentPackageFingerprint { get; set; }
    public Dictionary<string, SourceDocument> Sources { get; } = new();
    public Dictionary<string, int> CurrentVersions { get; } = new();
    public List<ProjectEvent> Events { get; } = new();
    public List<DecisionRecord> Decisions { get; } = new();
    public List<AnalysisResult> Analyses { get; } = new();
    public string? ActiveThresholdSetId { get; set; }
    public string? ActiveConstraintGraphId { get; set; }
    public string? ActiveWaveformId { get; set; }
    public string? ActiveClockId { get; set; }
    public string? ActiveMeasurementModelId { get; set; }
    public int NextEventSeq => Events.Count + 1;

    public SourceDocument? Current(string kind, string subject)
    {
        var key = SourceIdentity(kind, subject);
        return CurrentVersions.TryGetValue(key, out var version)
            ? Sources.GetValueOrDefault($"{key}:v{version}")
            : null;
    }

    public SourceDocument Get(string kind, string subject, int version) =>
        Sources[$"{SourceIdentity(kind, subject)}:v{version}"];

    public void Put(SourceDocument document)
    {
        Sources[$"{SourceIdentity(document.Kind.ToString(), document.Subject)}:v{document.Version}"] = document;
        CurrentVersions[SourceIdentity(document.Kind.ToString(), document.Subject)] = document.Version;
    }

    public IEnumerable<SourceDocument> CurrentSources() =>
        CurrentVersions
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Sources[$"{kv.Key}:v{kv.Value}"]);

    public static string SourceIdentity(string kind, string subject) => $"{kind}:{subject}";
}
