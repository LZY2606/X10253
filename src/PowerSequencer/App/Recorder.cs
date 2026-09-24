using System.Text.Json;
using System.Text.Json.Nodes;
using PowerSequencer.Domain;
using PowerSequencer.Persistence;

namespace PowerSequencer.App;

/// <summary>
/// Records domain mutations as machine-readable events and keeps derived
/// results consistent: a local change only invalidates results that depend on
/// it; stale results stay visible but never pose as current.
/// </summary>
public sealed class Recorder
{
    private readonly Store _store;
    private readonly string _project;

    public Recorder(Store store, string project)
    {
        _store = store;
        _project = project;
    }

    public EventRecord ImportWaveform(WaveformData wf)
    {
        var streamId = Hashing.ShortId("wf", wf.Rail.ToLowerInvariant(), 12);
        var data = JsonSerializer.SerializeToNode(wf, Store.JsonOptions)!;
        var ev = _store.Append(_project, "input.imported", new JsonObject
        {
            ["streamId"] = streamId,
            ["kind"] = wf.SignalKind,
            ["data"] = data,
            ["hash"] = Hashing.Sha256(data.ToJsonString(Store.JsonOptions))
        });
        Analyzer.Run(_store, _project);
        return ev;
    }

    public EventRecord ReviseWaveform(string streamId, WaveformData wf)
    {
        var proj = _store.Project(_project);
        var latest = proj.LatestInput(streamId)
            ?? throw new InvalidOperationException($"unknown input stream '{streamId}'");
        var data = JsonSerializer.SerializeToNode(wf, Store.JsonOptions)!;
        var ev = _store.Append(_project, "input.revised", new JsonObject
        {
            ["streamId"] = streamId,
            ["parentVersion"] = latest.Version,
            ["kind"] = wf.SignalKind,
            ["data"] = data,
            ["hash"] = Hashing.Sha256(data.ToJsonString(Store.JsonOptions))
        });
        InvalidateResults(r => r.Sources.Any(s => s.Kind == "waveform" && s.Id == streamId),
            $"input {streamId} revised to v{latest.Version + 1}", ev.Seq);
        Analyzer.Run(_store, _project);
        return ev;
    }

    public EventRecord CreateConstraints(ConstraintSet cs, string? id = null)
    {
        id ??= Hashing.ShortId("cs", Engine.EdgesFingerprint(cs.Edges) + string.Join(",", cs.Rails.Select(Engine.RailFingerprint)), 12);
        var data = JsonSerializer.SerializeToNode(cs, Store.JsonOptions)!;
        var ev = _store.Append(_project, "constraints.created", new JsonObject
        {
            ["id"] = id,
            ["parentVersion"] = null,
            ["data"] = data,
            ["fingerprint"] = ConstraintSetFingerprint(cs)
        });
        Analyzer.Run(_store, _project);
        return ev;
    }

    /// <summary>Fork a new threshold version from a parent; only dependent results go stale.</summary>
    public EventRecord ForkConstraints(string parentId, ConstraintSet cs)
    {
        var proj = _store.Project(_project);
        var parent = proj.Constraints.Where(c => c.Id == parentId).OrderBy(c => c.Version).LastOrDefault()
            ?? throw new InvalidOperationException($"unknown constraint set '{parentId}'");
        var parentCs = parent.Data.Deserialize<ConstraintSet>(Store.JsonOptions)!;
        var data = JsonSerializer.SerializeToNode(cs, Store.JsonOptions)!;
        var ev = _store.Append(_project, "constraints.created", new JsonObject
        {
            ["id"] = parentId,
            ["parentVersion"] = parent.Version,
            ["data"] = data,
            ["fingerprint"] = ConstraintSetFingerprint(cs)
        });

        // Partial staleness: compare per-rail and edge fingerprints old vs new.
        var staleRails = cs.Rails
            .Where(rc => parentCs.ForRail(rc.Rail) is not { } old || Engine.RailFingerprint(old) != Engine.RailFingerprint(rc))
            .Select(rc => rc.Rail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedRails = parentCs.Rails.Select(r => r.Rail)
            .Where(r => cs.ForRail(r) is null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        staleRails.UnionWith(removedRails);
        var edgesChanged = Engine.EdgesFingerprint(parentCs.Edges) != Engine.EdgesFingerprint(cs.Edges);

        InvalidateResults(r =>
            (r.Kind == "rail-analysis" && r.Rail is not null && staleRails.Contains(r.Rail)) ||
            (r.Kind is "sequence-analysis" or "graph-analysis" or "first-violation" &&
             (edgesChanged || r.Sources.Any(s => s.Kind == "constraints" &&
                 staleRails.Contains(s.Id[(s.Id.IndexOf(':') + 1)..])))),
            $"constraints {parentId} forked to v{parent.Version + 1}; affected rails: {string.Join(",", staleRails)}",
            ev.Seq);
        Analyzer.Run(_store, _project);
        return ev;
    }

    public EventRecord Decide(string target, string action, string rationale)
    {
        var proj = _store.Project(_project);
        var existing = proj.Decisions.Where(d => d.Target == target && !d.Retracted).ToList();
        var conflicts = existing.Where(d => d.Action != action).Select(d => d.Id).ToList();
        var id = Hashing.ShortId("dec", $"{target}|{action}|{rationale}|{proj.EventLog.Count}", 12);
        return _store.Append(_project, "decision.made", new JsonObject
        {
            ["id"] = id,
            ["target"] = target,
            ["action"] = action, // "accept" | "reject" | "adjust"
            ["rationale"] = rationale,
            ["conflictsWith"] = new JsonArray(conflicts.Select(c => JsonValue.Create(c)).ToArray())
        });
    }

    public EventRecord RetractDecision(string decisionId)
    {
        return _store.Append(_project, "decision.retracted", new JsonObject { ["id"] = decisionId });
    }

    private void InvalidateResults(Func<StoredResult, bool> predicate, string reason, long bySeq)
    {
        var proj = _store.Project(_project);
        var ids = proj.Results.Where(r => r.Status == "current").Where(predicate).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        _store.Append(_project, "result.invalidated", new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(i => JsonValue.Create(i)).ToArray()),
            ["reason"] = reason,
            ["bySeq"] = bySeq
        });
    }

    public static string ConstraintSetFingerprint(ConstraintSet cs) => Hashing.Sha256(string.Join("&",
        cs.Rails.Select(Engine.RailFingerprint).OrderBy(s => s, StringComparer.Ordinal))
        + "|" + Engine.EdgesFingerprint(cs.Edges));
}
