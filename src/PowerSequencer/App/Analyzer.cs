using System.Text.Json;
using System.Text.Json.Nodes;
using PowerSequencer.Domain;
using PowerSequencer.Persistence;

namespace PowerSequencer.App;

/// <summary>
/// Computes derived results from current projections. Every derived value
/// carries algorithm version, parameter fingerprint and direct sources.
/// Deterministic ids make recomputation idempotent and replayable.
/// </summary>
public static class Analyzer
{
    public static void Run(Store store, string project)
    {
        var proj = store.Project(project);
        var csV = proj.LatestConstraints();
        if (csV is null) return;
        var cs = csV.Data.Deserialize<ConstraintSet>(Store.JsonOptions)!;

        var waveforms = proj.Inputs
            .GroupBy(i => i.StreamId)
            .Select(g => g.OrderBy(i => i.Version).Last())
            .Select(i => (stream: i, wf: i.Data.Deserialize<WaveformData>(Store.JsonOptions)!))
            .ToList();
        if (waveforms.Count == 0) return;

        var newResults = new List<JsonNode>();
        var revalidate = new List<string>();

        void Emit(string kind, string? rail, string fingerprint, List<SourceRef> sources, object payload, string resultId)
        {
            var existing = proj.Result(resultId);
            if (existing is not null)
            {
                if (existing.Status == "stale") revalidate.Add(resultId);
                return;
            }
            var node = JsonSerializer.SerializeToNode(payload, Store.JsonOptions)!;
            node["id"] = resultId;
            node["kind"] = kind;
            node["rail"] = rail;
            node["status"] = "current";
            newResults.Add(node);
        }

        // ---- per-rail analysis ----
        var railResults = new List<(RailAnalysisResult res, string id, WaveformData wf)>();
        foreach (var rc in cs.Rails)
        {
            var match = waveforms
                .Where(w => string.Equals(w.wf.Rail, rc.Rail, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.wf.SignalKind == "voltage" ? 1 : 0)
                .ThenByDescending(w => w.stream.Version)
                .FirstOrDefault();
            if (match.wf is null) continue;
            var res = Engine.AnalyzeRail(match.wf, rc, csV.Id, csV.Version, match.stream.StreamId, match.stream.Version);
            var id = Hashing.ShortId("res-rail",
                $"{rc.Rail}|{Engine.AlgoVersion}|{res.ParamFingerprint}|{match.stream.StreamId}@v{match.stream.Version}");
            railResults.Add((res, id, match.wf));
            Emit("rail-analysis", rc.Rail, res.ParamFingerprint, res.Sources, res, id);
        }

        // ---- rise/fall edge times per signal ----
        var riseTimes = new Dictionary<string, Engine.EdgeTime>(StringComparer.Ordinal);
        var fallTimes = new Dictionary<string, Engine.EdgeTime>(StringComparer.Ordinal);
        foreach (var (res, _, wf) in railResults)
        {
            var tol = Engine.SignalTolerance(wf);
            var rise = res.Windows.FirstOrDefault(w => w.State == RailState.High);
            if (rise is not null)
                riseTimes[res.Rail] = new Engine.EdgeTime(res.Rail, rise.TStart, tol);
            var fall = res.Windows.FirstOrDefault(w => w.State == RailState.Low && rise is not null && w.TStart > rise.TStart);
            if (fall is not null)
                fallTimes[res.Rail] = new Engine.EdgeTime(res.Rail, fall.TStart, tol);
        }

        var allWfSources = waveforms.Select(w => new SourceRef("waveform", w.stream.StreamId, w.stream.Version))
            .Cast<SourceRef>().ToList();
        var wfVersionKey = string.Join(",", waveforms.Select(w => $"{w.stream.StreamId}@v{w.stream.Version}").OrderBy(s => s, StringComparer.Ordinal));

        // ---- sequence analysis over voltage rails ----
        var voltageRails = railResults.Where(r =>
            string.Equals(r.wf.SignalKind, "voltage", StringComparison.OrdinalIgnoreCase)).ToList();
        var seqSources = allWfSources
            .Concat(cs.Rails.Select(rc => new SourceRef("constraints", $"{csV.Id}:{rc.Rail}", csV.Version)))
            .ToList();
        var seqFp = Hashing.Sha256("seq|" + Engine.AlgoVersion + "|" + string.Join("&",
            cs.Rails.Select(Engine.RailFingerprint).OrderBy(s => s, StringComparer.Ordinal)));
        var seq = Engine.AnalyzeSequence(
            voltageRails.Where(r => riseTimes.ContainsKey(r.res.Rail)).Select(r => riseTimes[r.res.Rail]).ToList(),
            voltageRails.Where(r => fallTimes.ContainsKey(r.res.Rail)).Select(r => fallTimes[r.res.Rail]).ToList(),
            seqSources, seqFp);
        var seqId = Hashing.ShortId("res-seq", $"{Engine.AlgoVersion}|{seqFp}|{wfVersionKey}");
        Emit("sequence-analysis", null, seqFp, seqSources, seq, seqId);

        // ---- dependency graph analysis ----
        var graphDiags = new List<Diagnostic>();
        var verdicts = Graph.Verdicts(cs.Edges, riseTimes, graphDiags);
        var cycles = Graph.MinimalConflictEdgeSets(cs.Edges);
        var railIdKey = string.Join("|", railResults.Select(r => r.id).OrderBy(s => s, StringComparer.Ordinal));
        var graphFp = Hashing.Sha256("graph|" + Engine.AlgoVersion + "|" + Engine.EdgesFingerprint(cs.Edges) + "|" + railIdKey);
        var graph = new GraphAnalysisResult(Engine.AlgoVersion, graphFp, seqSources, verdicts, cycles, graphDiags);
        var graphId = Hashing.ShortId("res-graph", $"{Engine.AlgoVersion}|{graphFp}|{wfVersionKey}");
        Emit("graph-analysis", null, graphFp, seqSources, graph, graphId);

        // ---- first violation evidence ----
        var candidates = new List<(double at, string kind, string desc, string resultId, List<SourceRef> src)>();
        foreach (var (res, id, _) in railResults)
            foreach (var ex in res.Excursions.Where(e => e.Classification == ExcursionClass.Violation))
                candidates.Add((ex.TStart, "excursion",
                    $"{ex.Rail} {ex.Direction} violation: {ex.Reason}", id, res.Sources));
        foreach (var v in verdicts.Where(v => v.Verdict == "violated"))
            candidates.Add((v.At ?? double.MaxValue, "dependency",
                $"edge {v.Edge.From}->{v.Edge.To} violated: {v.Reason}", graphId, graph.Sources));
        foreach (var m in seq.Mismatches)
            candidates.Add((m.At, "shutdown-order", m.Reason, seqId, seq.Sources));

        var considered = candidates.Select(c => c.desc).ToList();
        FirstViolation? first = null;
        if (candidates.Count > 0)
        {
            var c = candidates.OrderBy(x => x.at).First();
            first = new FirstViolation(c.at, c.kind, c.desc, c.resultId, c.src);
        }
        var firstFp = Hashing.Sha256("first|" + string.Join("|", considered));
        var firstId = Hashing.ShortId("res-first", string.Join("|", railResults.Select(r => r.id)) + seqId + graphId);
        Emit("first-violation", null, firstFp, seqSources,
            new FirstViolationResult(Engine.AlgoVersion, firstFp, seqSources, first, considered), firstId);

        if (newResults.Count > 0)
            store.Append(project, "analysis.computed", new JsonObject
            {
                ["results"] = new JsonArray(newResults.ToArray())
            });
        if (revalidate.Count > 0)
            store.Append(project, "result.revalidated", new JsonObject
            {
                ["ids"] = new JsonArray(revalidate.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray())
            });
    }
}
