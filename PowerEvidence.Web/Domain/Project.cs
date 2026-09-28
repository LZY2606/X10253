using System.Text.Json;

namespace PowerEvidence.Domain;

public enum EventType
{
    ProjectCreated,
    SourceSubmitted,
    RulesRevised,
    AnalysisRecorded,
    DecisionRecorded
}

public sealed record StoredEvent(
    string Id,
    int Ordinal,
    string ProjectId,
    EventType Type,
    string ParentEventId,
    string AtVirtualTime,
    string PayloadJson,
    string? OriginalInputId);

public sealed record ExportBundle(
    string BundleId,
    string ExportedAt,
    string ProjectId,
    string ProjectName,
    IReadOnlyList<StoredEvent> Events,
    IReadOnlyList<OriginalInput> OriginalInputs);

public sealed record OriginalInput(
    string Id,
    string EventId,
    string MediaType,
    string Json);

public sealed record SourceVersion(
    string VersionId,
    string ParentVersionId,
    int EventOrdinal,
    string Kind,
    JsonElement Body,
    string Summary);

public sealed record RuleVersion(
    string VersionId,
    string ParentVersionId,
    int EventOrdinal,
    JsonElement Body,
    string Summary);

public sealed record ProjectDocument(
    string Id,
    string Name,
    string CreatedEventId,
    IReadOnlyList<StoredEvent> Events,
    IReadOnlyList<OriginalInput> Inputs,
    IReadOnlyList<SourceVersion> Sources,
    IReadOnlyList<RuleVersion> Rules,
    IReadOnlyList<AnalysisRun> Runs,
    IReadOnlyList<DecisionRecord> Decisions)
{
    public StoredEvent? EventAt(string eventId) =>
        Events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase));

    public ProjectView ViewAt(string? eventId = null)
    {
        var target = string.IsNullOrWhiteSpace(eventId)
            ? Events[^1]
            : EventAt(eventId) ?? throw new InvalidOperationException($"unknown event {eventId}");
        var targetOrdinal = target.Ordinal;

        var sources = Sources.Where(s => s.EventOrdinal <= targetOrdinal).ToList();
        var rules = Rules.Where(s => s.EventOrdinal <= targetOrdinal).ToList();
        var runs = Runs.Where(s => s.AtOrdinal <= targetOrdinal).ToList();
        var decisions = Decisions.Where(s => s.Ordinal <= targetOrdinal).ToList();
        var inputs = Inputs.Where(i => Events.Any(e => e.Id == i.EventId && e.Ordinal <= targetOrdinal)).ToList();

        var activeSource = LatestMap(sources);
        var activeRules = LatestMap(rules);
        var effectiveDecisionByStableId = EffectiveDecisions(decisions);

        var visibleResults = runs.SelectMany(run => run.Results.Select(result =>
        {
            var decision = decisions
                .Where(d => d.StableResultId == result.StableId && d.Ordinal <= targetOrdinal)
                .OrderByDescending(d => d.Ordinal)
                .FirstOrDefault();

            var status = result.Status;
            if (decision is not null)
            {
                status = decision.Kind switch
                {
                    DecisionKind.Accept or DecisionKind.AcceptCycleEdgeSet => ResultStatus.Accepted,
                    DecisionKind.Reject => ResultStatus.Rejected,
                    _ => status
                };

                if (result.Kind == "cycle-dependency" && decision.EdgeSet is not null)
                {
                    var otherAccepted = decisions.Any(d =>
                        d.EventId != decision.EventId &&
                        d.StableResultId == result.StableId &&
                        d.Ordinal <= targetOrdinal &&
                        d.Kind == DecisionKind.AcceptCycleEdgeSet &&
                        d.EdgeSet is not null &&
                        !d.EdgeSet.SequenceEqual(decision.EdgeSet));
                    if (otherAccepted)
                        status = ResultStatus.Conflict;
                }
            }

            var isCurrent = run.AtOrdinal == runs.Max(r => r.AtOrdinal) &&
                            result.SourceVersions.All(v => activeSource.Values.Any(sv => sv.VersionId == v)) &&
                            result.RuleVersion == activeRules.GetValueOrDefault("thresholds")?.VersionId;

            if (!isCurrent)
                status = ResultStatus.Stale;

            return new VisibleResult(result, run.RunId, run.AtOrdinal, isCurrent, decision);
        })).ToList();

        var firstViolation = visibleResults
            .Where(r => r.IsCurrent && (r.Result.Status == ResultStatus.Candidate ||
                                        r.Result.Status == ResultStatus.Accepted ||
                                        r.EffectiveDecision is not null))
            .OrderBy(r => r.Result.FirstTime ?? double.MaxValue)
            .ThenBy(r => r.Result.StableId, StringComparer.Ordinal)
            .FirstOrDefault();

        return new ProjectView(
            this,
            target,
            sources,
            rules,
            runs,
            decisions,
            inputs,
            activeSource,
            activeRules,
            visibleResults,
            firstViolation);
    }

    private static Dictionary<string, T> LatestMap<T>(IReadOnlyList<T> versions) where T : notnull
    {
        return versions
            .Select((version, index) => (version, index))
            .GroupBy(_ => typeof(T).Name)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.index).Last().version);
    }

    private static Dictionary<string, SourceVersion> LatestMap(IReadOnlyList<SourceVersion> versions)
    {
        return versions.GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.OrderBy(v => v.EventOrdinal).Last());
    }

    private static Dictionary<string, RuleVersion> LatestMap(IReadOnlyList<RuleVersion> versions)
    {
        return versions.GroupBy(v => v.Body.TryGetProperty("scope", out var s) ? s.GetString() ?? "thresholds" : "thresholds")
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.EventOrdinal).Last());
    }

    private static Dictionary<string, DecisionRecord> EffectiveDecisions(IReadOnlyList<DecisionRecord> decisions)
    {
        return decisions
            .OrderBy(d => d.Ordinal)
            .GroupBy(d => d.StableResultId)
            .ToDictionary(g => g.Key, g => g.Last());
    }
}

public sealed record VisibleResult(
    AnalysisResult Result,
    string RunId,
    int RunOrdinal,
    bool IsCurrent,
    DecisionRecord? EffectiveDecision);

public sealed record ProjectView(
    ProjectDocument Project,
    StoredEvent AtEvent,
    IReadOnlyList<SourceVersion> SourceVersions,
    IReadOnlyList<RuleVersion> RuleVersions,
    IReadOnlyList<AnalysisRun> Runs,
    IReadOnlyList<DecisionRecord> Decisions,
    IReadOnlyList<OriginalInput> Inputs,
    IReadOnlyDictionary<string, SourceVersion> ActiveSource,
    IReadOnlyDictionary<string, RuleVersion> ActiveRules,
    IReadOnlyList<VisibleResult> Results,
    VisibleResult? FirstViolation);
