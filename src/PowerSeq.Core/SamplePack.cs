using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerSeq.Core;

/// <summary>离线案例包：基线（原波形/阈值/滞回/最小稳定时间/依赖图/采样时钟/测量误差）+ 脚本化修订。</summary>
public sealed record SamplePack(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("clock")] ClockSpec Clock,
    [property: JsonPropertyName("thresholds")] SamplePack.ThresholdSection Thresholds,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<DependencyEdge> Dependencies,
    [property: JsonPropertyName("sources")] IReadOnlyList<SamplePack.SourceSection> Sources,
    [property: JsonPropertyName("script")] IReadOnlyList<SamplePack.ScriptOp>? Script)
{
    public sealed record ThresholdSection(
        [property: JsonPropertyName("setId")] string SetId,
        [property: JsonPropertyName("rails")] IReadOnlyList<RailThreshold> Rails);

    public sealed record SourceSection(
        [property: JsonPropertyName("sourceId")] string SourceId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] SourceKind Kind,
        [property: JsonPropertyName("rail")] string Rail,
        [property: JsonPropertyName("samples")] IReadOnlyList<RawSample> Samples);

    public sealed record ScriptOp(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("rails")] IReadOnlyList<RailThreshold>? Rails,
        [property: JsonPropertyName("parent")] string? Parent,
        [property: JsonPropertyName("sourceId")] string? SourceId,
        [property: JsonPropertyName("samples")] IReadOnlyList<RawSample>? Samples,
        [property: JsonPropertyName("edges")] IReadOnlyList<DependencyEdge>? Edges,
        [property: JsonPropertyName("candidateKind")] string? CandidateKind,
        [property: JsonPropertyName("edge")] string? Edge,
        [property: JsonPropertyName("verdict")] Verdict? Verdict,
        [property: JsonPropertyName("rationale")] string? Rationale,
        [property: JsonPropertyName("chosen")] int? Chosen);
}

public static class SamplePackLoader
{
    public const string ExpectedFormat = "powerseq-casepack/1";

    /// <summary>解析并校验案例包；格式错误给出明确诊断。</summary>
    public static SamplePack Parse(string json)
    {
        SamplePack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<SamplePack>(json, JsonUtil.Options);
        }
        catch (JsonException e)
        {
            throw new FormatException($"case pack is not valid JSON: {e.Message}");
        }
        if (pack is null) throw new FormatException("case pack is empty");
        if (pack.Format != ExpectedFormat)
            throw new FormatException($"format must be '{ExpectedFormat}', got '{pack.Format}'");
        if (string.IsNullOrWhiteSpace(pack.Id)) throw new FormatException("missing id");
        if (string.IsNullOrWhiteSpace(pack.Title)) throw new FormatException("missing title");
        if (pack.Clock is null) throw new FormatException("missing clock (nominalPeriodNs/gapFactor/simultaneityWindowNs/measurementErrorV)");
        if (pack.Clock.NominalPeriodNs <= 0) throw new FormatException("clock.nominalPeriodNs must be positive");
        if (pack.Clock.GapFactor <= 1) throw new FormatException("clock.gapFactor must be > 1");
        if (pack.Thresholds is null || pack.Thresholds.Rails.Count == 0)
            throw new FormatException("missing thresholds.rails");
        foreach (var r in pack.Thresholds.Rails) r.Validate();
        if (pack.Sources is null || pack.Sources.Count == 0)
            throw new FormatException("missing sources");
        foreach (var s in pack.Sources)
        {
            if (string.IsNullOrWhiteSpace(s.SourceId)) throw new FormatException("source missing sourceId");
            if (s.Samples is null || s.Samples.Count == 0)
                throw new FormatException($"source {s.SourceId}: samples must be non-empty");
        }
        if (pack.Dependencies is null) throw new FormatException("missing dependencies");
        return pack;
    }

    /// <summary>把案例包重放为事件流：基线导入 + 脚本化修订/裁定，全部留痕。</summary>
    public static string Load(CaseStore store, SamplePack pack, string actor)
    {
        store.CreateCase(pack.Id, actor, pack.Title, pack.Clock);
        foreach (var s in pack.Sources)
            store.ImportSource(pack.Id, actor, s.SourceId, s.Name, s.Kind, s.Rail, new SourcePayload(s.Samples, "baseline import"));
        store.ImportThresholds(pack.Id, actor, pack.Thresholds.SetId, new ThresholdPayload(pack.Thresholds.Rails), "baseline thresholds v1");
        store.ImportDependencies(pack.Id, actor, null, new DependencyPayload(pack.Dependencies));

        foreach (var op in pack.Script ?? Array.Empty<SamplePack.ScriptOp>())
        {
            var opActor = op.Actor ?? actor;
            switch (op.Op)
            {
                case "fork-thresholds":
                {
                    if (op.Rails is null) throw new FormatException("fork-thresholds requires rails");
                    foreach (var r in op.Rails) r.Validate();
                    var view = Projector.Project(store, pack.Id);
                    var set = view.ThresholdSets.First();
                    var parent = op.Parent is null
                        ? set.CurrentVersionId
                        : set.Versions.First(v => v.Version == int.Parse(op.Parent)).VersionId;
                    store.ForkThresholds(pack.Id, opActor, set.SetId, parent, new ThresholdPayload(op.Rails), op.Note);
                    break;
                }
                case "revise-source":
                {
                    if (op.SourceId is null || op.Samples is null)
                        throw new FormatException("revise-source requires sourceId and samples");
                    var view = Projector.Project(store, pack.Id);
                    var src = view.Sources.First(s => s.SourceId == op.SourceId);
                    store.ReviseSource(pack.Id, opActor, op.SourceId, src.CurrentVersionId,
                        new SourcePayload(op.Samples, op.Note));
                    break;
                }
                case "revise-dependencies":
                {
                    if (op.Edges is null) throw new FormatException("revise-dependencies requires edges");
                    var view = Projector.Project(store, pack.Id);
                    store.ImportDependencies(pack.Id, opActor, view.CurrentDependencyVersionId,
                        new DependencyPayload(op.Edges));
                    break;
                }
                case "decide":
                {
                    if (op.Verdict is null || op.CandidateKind is null)
                        throw new FormatException("decide requires candidateKind and verdict");
                    var candidate = FindCandidate(store, pack.Id, op.CandidateKind, op.Edge);
                    store.Decide(pack.Id, opActor, candidate.Id, op.Verdict.Value, op.Rationale ?? "");
                    break;
                }
                case "resolve-conflict":
                {
                    if (op.CandidateKind is null) throw new FormatException("resolve-conflict requires candidateKind");
                    var view = Projector.Project(store, pack.Id);
                    var candidate = FindCandidate(store, pack.Id, op.CandidateKind, op.Edge);
                    var conflict = view.Conflicts.FirstOrDefault(c => c.CandidateId == candidate.Id && !c.Resolved)
                        ?? throw new FormatException($"no open conflict on candidate {candidate.Id}");
                    var chosen = conflict.DecisionIds[op.Chosen ?? 0];
                    store.ResolveConflict(pack.Id, opActor, candidate.Id, chosen, op.Rationale ?? "");
                    break;
                }
                case "revert-decision":
                {
                    var view = Projector.Project(store, pack.Id);
                    var target = view.Decisions.LastOrDefault(d => !d.Reverted && d.Actor == op.Actor)
                        ?? throw new FormatException($"no active decision by {op.Actor} to revert");
                    store.RevertDecision(pack.Id, opActor, target.DecisionId, op.Rationale ?? op.Note ?? "");
                    break;
                }
                default:
                    throw new FormatException($"unknown script op '{op.Op}'");
            }
        }
        return pack.Id;
    }

    private static Candidate FindCandidate(CaseStore store, string caseId, string kind, string? edge)
    {
        var view = Projector.Project(store, caseId);
        var evalId = view.CurrentOfKind("sequence-evaluation").FirstOrDefault()?.Id
            ?? throw new FormatException("no current sequence evaluation");
        var eval = JsonUtil.Parse<SequenceEvaluation>(
            view.Derived.First(d => d.Id == evalId).PayloadJson);
        var matches = eval.Candidates.Where(c => c.Kind == kind &&
            (edge is null || string.Join(",", c.Involved) == edge.Replace("->", ","))).ToList();
        return matches.FirstOrDefault()
            ?? throw new FormatException($"no open candidate of kind '{kind}'" + (edge is null ? "" : $" on edge {edge}"));
    }
}
