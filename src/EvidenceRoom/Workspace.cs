using System.Text.Json;

namespace EvidenceRoom;

/// <summary>Orchestrates import -> staleness -> recompute -> decisions -> replay.</summary>
public sealed class Workspace
{
    public Store Store { get; }

    public Workspace(Store store) => Store = store;

    // ---------- import ----------

    public sealed record ImportOutcome(InputVersion Input, List<string> Diagnostics, List<string> StaledResultIds);

    public ImportOutcome ImportText(string text)
    {
        var parsed = ImportParser.Parse(text); // explicit format errors throw FormatException
        var parent = Store.LatestInput(parsed.Kind);
        var input = Store.AddInput(parsed.Kind, parsed.Label, parsed.PayloadJson, parent?.Id);

        var changedRails = DiffRails(parsed.Kind, parent?.PayloadJson, parsed.PayloadJson);
        var staled = input.Seq == parent?.Seq ? new List<string>() : Store.MarkStale(parsed.Kind, changedRails);
        var ev = Store.AppendEvent("input-submitted", new
        {
            inputId = input.Id, kind = input.Kind, label = input.Label,
            parentId = input.ParentId, changedRails, diagnostics = parsed.Diagnostics,
            staledResultIds = staled
        });
        Recompute(ev.Seq);
        return new ImportOutcome(input, parsed.Diagnostics, staled);
    }

    /// <summary>Per-rail diff between two versions of the same kind; null means "everything" (global scope).</summary>
    private static HashSet<string>? DiffRails(string kind, string? oldJson, string newJson)
    {
        if (oldJson is null) return null; // first version: everything is new
        try
        {
            using var a = JsonDocument.Parse(oldJson);
            using var b = JsonDocument.Parse(newJson);
            var changed = new HashSet<string>();
            if (kind is "waveform" or "thresholds")
            {
                var prop = kind == "waveform" ? "Rails" : "Rails";
                var oldRails = a.RootElement.GetProperty(prop);
                var newRails = b.RootElement.GetProperty(prop);
                if (oldRails.ValueKind == JsonValueKind.Array)
                {
                    // waveform: array of rails keyed by Name
                    var oldMap = oldRails.EnumerateArray().ToDictionary(
                        r => r.GetProperty("Name").GetString()!, r => r.GetRawText());
                    foreach (var nr in newRails.EnumerateArray())
                    {
                        var name = nr.GetProperty("Name").GetString()!;
                        if (!oldMap.TryGetValue(name, out var old) || old != nr.GetRawText())
                            changed.Add(name);
                    }
                    // clock / error changes are global
                    if (a.RootElement.GetProperty("ClockInterval").GetRawText() != b.RootElement.GetProperty("ClockInterval").GetRawText()
                        || a.RootElement.GetProperty("VoltageError").GetRawText() != b.RootElement.GetProperty("VoltageError").GetRawText())
                        return null;
                }
                else
                {
                    // thresholds: object keyed by rail name
                    var oldMap = oldRails.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
                    foreach (var p in newRails.EnumerateObject())
                        if (!oldMap.TryGetValue(p.Name, out var old) || old != p.Value.GetRawText())
                            changed.Add(p.Name);
                }
                return changed;
            }
            return null; // dependencies changed: global scope
        }
        catch { return null; }
    }

    // ---------- recompute derived results ----------

    public void Recompute(long eventSeq)
    {
        var wave = Store.LatestInput("waveform");
        var thrs = Store.LatestInput("thresholds");
        var deps = Store.LatestInput("dependencies");
        if (wave is null || thrs is null) return;

        var wavePayload = J.De<JsonElement>(wave.PayloadJson);
        var thrPayload = J.De<JsonElement>(thrs.PayloadJson);
        var depPayload = deps is null ? (JsonElement?)null : J.De<JsonElement>(deps.PayloadJson);

        var clockInterval = wavePayload.GetProperty("ClockInterval").GetProperty("Normalized").GetDouble();
        var voltErr = wavePayload.GetProperty("VoltageError").GetProperty("Normalized").GetDouble();

        var thrMap = new Dictionary<string, RailThreshold>();
        foreach (var p in thrPayload.GetProperty("Rails").EnumerateObject())
        {
            Quantity Q(JsonElement e) => new(
                e.GetProperty("Raw").GetString()!, e.GetProperty("Unit").GetString()!, e.GetProperty("Dim").GetString()!)
            { Normalized = e.GetProperty("Normalized").ValueKind == JsonValueKind.Number ? e.GetProperty("Normalized").GetDouble() : null,
              Diagnostic = e.GetProperty("Diagnostic").ValueKind == JsonValueKind.String ? e.GetProperty("Diagnostic").GetString() : null };
            thrMap[p.Name] = new RailThreshold(Q(p.Value.GetProperty("Rise")), Q(p.Value.GetProperty("Fall")), Q(p.Value.GetProperty("MinStable")));
        }

        var edges = new List<DependencyEdge>();
        if (depPayload is not null)
            foreach (var e in depPayload.Value.GetProperty("Edges").EnumerateArray())
                edges.Add(new DependencyEdge(
                    e.GetProperty("From").GetString()!, e.GetProperty("To").GetString()!,
                    new Quantity(e.GetProperty("MinDelay").GetProperty("Raw").GetString()!, "", "time")
                    { Normalized = e.GetProperty("MinDelay").GetProperty("Normalized").ValueKind == JsonValueKind.Number
                        ? e.GetProperty("MinDelay").GetProperty("Normalized").GetDouble() : null },
                    e.GetProperty("Phase").GetString()!));

        var analyses = new Dictionary<string, Engine.RailAnalysis>();
        foreach (var r in wavePayload.GetProperty("Rails").EnumerateArray())
        {
            var name = r.GetProperty("Name").GetString()!;
            var samples = new List<SamplePoint>();
            foreach (var s in r.GetProperty("Samples").EnumerateArray())
                samples.Add(new SamplePoint(Q2(s.GetProperty("T")), Q2(s.GetProperty("V"))));
            var rail = new RailWaveform(name, r.GetProperty("Unit").GetString() ?? "V", samples, new(), new());
            if (!thrMap.TryGetValue(name, out var th))
            {
                analyses[name] = new Engine.RailAnalysis(name, new(), new(),
                    new() { $"{name}: 无阈值定义，状态窗口未知" }, null, null);
                continue;
            }
            analyses[name] = Engine.AnalyzeRail(rail, th, clockInterval, voltErr);

            var fingerprint = Ids.Fingerprint(new { rail = name, th, clockInterval, voltErr });
            Store.PutResult("rail-windows", fingerprint,
                new() { new("waveform", name, wave.Id), new("thresholds", name, thrs.Id) },
                J.Ser(J.Elem(analyses)), eventSeq);
        }

        var startup = Engine.CheckStartup(analyses, edges, clockInterval);
        var shutdown = Engine.CheckShutdown(analyses, edges, clockInterval);
        var cycles = Engine.CheckCycles(edges);

        var globalSources = new List<Store.Source> { new("waveform", null, wave.Id), new("thresholds", null, thrs.Id) };
        if (deps is not null) globalSources.Add(new("dependencies", null, deps.Id));

        Store.PutResult("startup-check", Ids.Fingerprint(new { edges, clockInterval }),
            globalSources, J.Ser(J.Elem(startup)), eventSeq);
        Store.PutResult("shutdown-check", Ids.Fingerprint(new { edges, clockInterval }),
            globalSources, J.Ser(J.Elem(shutdown)), eventSeq);
        Store.PutResult("cycle-check", Ids.Fingerprint(new { edges }),
            deps is null ? globalSources : new() { new("dependencies", null, deps.Id) },
            J.Ser(J.Elem(cycles)), eventSeq);

        // first violation evidence across all checks
        var firstViolation = FirstViolation(startup, shutdown, cycles, analyses);
        Store.PutResult("first-violation", Ids.Fingerprint(new { edges, clockInterval }),
            globalSources, J.Ser(J.Elem(firstViolation)), eventSeq);
    }

    private static Quantity Q2(JsonElement e) => new(
        e.GetProperty("Raw").GetString()!, e.GetProperty("Unit").GetString()!, e.GetProperty("Dim").GetString()!)
    {
        Normalized = e.GetProperty("Normalized").ValueKind == JsonValueKind.Number ? e.GetProperty("Normalized").GetDouble() : null,
        Diagnostic = e.GetProperty("Diagnostic").ValueKind == JsonValueKind.String ? e.GetProperty("Diagnostic").GetString() : null
    };

    public sealed record FirstViolationEvidence(
        string Kind, string Subject, string Detail, string Basis,
        List<Store.Source> Sources, string AlgoVersion, string Fingerprint);

    private static object FirstViolation(
        Engine.SequenceReport startup, Engine.SequenceReport shutdown, Engine.CycleReport cycles,
        Dictionary<string, Engine.RailAnalysis> analyses)
    {
        if (cycles.HasCycle)
            return new
            {
                found = true, kind = "cyclic-dependency", subject = "依赖图",
                detail = $"循环依赖：最小矛盾边集 {cycles.MinimalEdgeSets.Count} 个合法候选：" +
                         string.Join("；", cycles.MinimalEdgeSets.Select(s => "[" + string.Join(", ", s) + "]")),
                basis = "minimal-feedback-edge-set; all minimum candidates retained",
                status = cycles.Status
            };
        if (startup.FirstViolation is not null)
            return new
            {
                found = true, kind = "startup-sequence", subject = startup.FirstViolation.Label,
                detail = startup.FirstViolation.Detail, basis = startup.FirstViolation.Basis,
                status = "violation"
            };
        if (shutdown.FirstViolation is not null)
            return new
            {
                found = true, kind = "shutdown-reverse-order", subject = shutdown.FirstViolation.Label,
                detail = shutdown.FirstViolation.Detail, basis = shutdown.FirstViolation.Basis,
                status = "violation"
            };
        var undetermined = startup.Candidates.Concat(shutdown.Candidates)
            .Where(c => c.Status is "undetermined" or "unknown").ToList();
        if (undetermined.Count > 0)
            return new
            {
                found = false, kind = "undetermined", subject = undetermined[0].Label,
                detail = "无违约；存在无法唯一决定的候选：" + string.Join("；", undetermined.Select(u => u.Label)),
                basis = "no-violation; undetermined candidates retained", status = "undetermined"
            };
        return new { found = false, kind = "none", subject = "-", detail = "无违约", basis = "all-checks-pass", status = "ok" };
    }

    // ---------- decisions / conflicts / undo ----------

    public EventRecord Decide(string targetResultId, string verdict, string reviewer, string note, long[]? resolves = null)
        => Store.AppendEvent("decision", new
        {
            target = targetResultId, verdict, reviewer, note,
            resolves = resolves ?? Array.Empty<long>()
        });

    public EventRecord Undo(long targetEventSeq)
        => Store.AppendEvent("undo", new { targetEventSeq });

    public sealed record DecisionView(long Seq, string Id, string Target, string Verdict, string Reviewer,
        string Note, bool Undone, bool Resolved);

    public List<DecisionView> Decisions(long? asOfSeq = null)
    {
        var events = Store.Events(asOfSeq);
        var undone = events.Where(e => e.Kind == "undo")
            .Select(e => J.De<JsonElement>(e.PayloadJson).GetProperty("targetEventSeq").GetInt64())
            .ToHashSet();
        var resolved = events.Where(e => e.Kind == "decision")
            .SelectMany(e => J.De<JsonElement>(e.PayloadJson).TryGetProperty("resolves", out var r)
                ? r.EnumerateArray().Select(x => x.GetInt64()) : Enumerable.Empty<long>())
            .ToHashSet();
        var list = new List<DecisionView>();
        foreach (var e in events.Where(e => e.Kind == "decision"))
        {
            var p = J.De<JsonElement>(e.PayloadJson);
            list.Add(new DecisionView(e.Seq, e.Id,
                p.GetProperty("target").GetString()!,
                p.GetProperty("verdict").GetString()!,
                p.GetProperty("reviewer").GetString()!,
                p.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
                undone.Contains(e.Seq), resolved.Contains(e.Seq)));
        }
        return list;
    }

    /// <summary>Open conflicts: same target, opposing verdicts, neither undone nor resolved.</summary>
    public List<(string Target, List<DecisionView> Sides)> Conflicts(long? asOfSeq = null)
        => Decisions(asOfSeq)
            .Where(d => !d.Undone && !d.Resolved)
            .GroupBy(d => d.Target)
            .Where(g => g.Select(d => d.Verdict).Distinct().Count() > 1)
            .Select(g => (g.Key, g.ToList()))
            .ToList();
}
