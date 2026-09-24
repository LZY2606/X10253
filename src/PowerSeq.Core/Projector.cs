namespace PowerSeq.Core;

/// <summary>
/// 投影器：把事件流重放到指定 seq，重建当时的源版本、阈值版本、依赖版本、
/// 派生结果（含算法版本/参数指纹/直接来源）与人工决定。
/// 派生结果按内容寻址：输入未变则身份不变、保持“当前”；输入变化只使依赖它的结果过期。
/// </summary>
public static class Projector
{
    private sealed class SourceBuilder
    {
        public required string Name;
        public SourceKind Kind;
        public required string Rail;
        public List<SourceVersionView> Versions = new();
        public string CurrentVersionId = "";
    }

    private sealed class ThresholdSetBuilder
    {
        public List<ThresholdVersionView> Versions = new();
        public string CurrentVersionId = "";
    }

    private sealed record Generation(
        IReadOnlyDictionary<string, string> RailSourceVersions, // rail -> sourceVersionId
        string? ThresholdVersionId,
        string? DependencyVersionId);

    public static CaseView Project(CaseStore store, string caseId, long? atSeq = null)
    {
        var events = store.ReadEvents(caseId, atSeq);
        var latestSeq = store.ReadEvents(caseId).Select(e => e.Seq).DefaultIfEmpty(0).Max();

        string title = caseId;
        ClockSpec? clock = null;
        var sources = new Dictionary<string, SourceBuilder>();
        var thresholdSets = new Dictionary<string, ThresholdSetBuilder>();
        var depVersions = new List<DependencyVersionView>();
        string? currentDepVersionId = null;
        var decisions = new List<DecisionView>();
        var reverted = new HashSet<string>();
        var resolutions = new List<ConflictResolvedPayload>();
        var generations = new List<Generation>();

        Generation CurrentGeneration() => new(
            sources.Where(kv => kv.Value.Kind == SourceKind.RailVoltage)
                   .ToDictionary(kv => kv.Value.Rail, kv => kv.Value.CurrentVersionId),
            thresholdSets.Values.FirstOrDefault()?.CurrentVersionId,
            currentDepVersionId);

        foreach (var ev in events)
        {
            var structural = true;
            switch (ev.Type)
            {
                case "case-created":
                {
                    var p = JsonUtil.Parse<CaseCreatedPayload>(ev.Payload);
                    title = p.Title; clock = p.Clock;
                    break;
                }
                case "source-imported":
                {
                    var p = JsonUtil.Parse<SourceImportedPayload>(ev.Payload);
                    var b = new SourceBuilder { Name = p.Name, Kind = p.Kind, Rail = p.Rail };
                    b.Versions.Add(new SourceVersionView(p.VersionId, null, 1, p.Payload.Note, ev.Seq, p.Payload));
                    b.CurrentVersionId = p.VersionId;
                    sources[p.SourceId] = b;
                    break;
                }
                case "source-revised":
                {
                    var p = JsonUtil.Parse<SourceRevisedPayload>(ev.Payload);
                    if (sources.TryGetValue(p.SourceId, out var b))
                    {
                        b.Versions.Add(new SourceVersionView(p.VersionId, p.ParentVersionId, b.Versions.Count + 1, p.Payload.Note, ev.Seq, p.Payload));
                        b.CurrentVersionId = p.VersionId;
                    }
                    break;
                }
                case "thresholds-imported":
                {
                    var p = JsonUtil.Parse<ThresholdsImportedPayload>(ev.Payload);
                    var b = new ThresholdSetBuilder();
                    b.Versions.Add(new ThresholdVersionView(p.VersionId, null, 1, p.Note, ev.Seq, p.Payload));
                    b.CurrentVersionId = p.VersionId;
                    thresholdSets[p.SetId] = b;
                    break;
                }
                case "thresholds-forked":
                {
                    var p = JsonUtil.Parse<ThresholdsForkedPayload>(ev.Payload);
                    if (thresholdSets.TryGetValue(p.SetId, out var b))
                    {
                        b.Versions.Add(new ThresholdVersionView(p.VersionId, p.ParentVersionId, b.Versions.Count + 1, p.Note, ev.Seq, p.Payload));
                        b.CurrentVersionId = p.VersionId;
                    }
                    break;
                }
                case "dependencies-imported":
                {
                    var p = JsonUtil.Parse<DependenciesImportedPayload>(ev.Payload);
                    depVersions.Add(new DependencyVersionView(p.VersionId, p.ParentVersionId, depVersions.Count + 1, ev.Seq, p.Payload));
                    currentDepVersionId = p.VersionId;
                    break;
                }
                case "decision-made":
                {
                    structural = false;
                    var p = JsonUtil.Parse<DecisionMadePayload>(ev.Payload);
                    decisions.Add(new DecisionView(p.DecisionId, p.CandidateId, p.Verdict, p.Rationale, ev.Actor, ev.Seq, false, false));
                    break;
                }
                case "decision-reverted":
                {
                    structural = false;
                    var p = JsonUtil.Parse<DecisionRevertedPayload>(ev.Payload);
                    reverted.Add(p.DecisionId);
                    break;
                }
                case "conflict-resolved":
                {
                    structural = false;
                    resolutions.Add(JsonUtil.Parse<ConflictResolvedPayload>(ev.Payload));
                    break;
                }
                default:
                    structural = false;
                    break;
            }
            if (structural)
                generations.Add(CurrentGeneration());
        }

        // ---- 派生计算：对每一个曾经“当前”的代次计算派生结果（内容寻址，去重）----
        var derived = new Dictionary<string, DerivedResult>();
        var thresholdVersionsById = thresholdSets.Values
            .SelectMany(s => s.Versions).ToDictionary(v => v.VersionId, v => v);
        var sourceVersionsById = sources.Values
            .SelectMany(s => s.Versions.Select(v => (s: sources.First(x => x.Value == s), v)))
            .ToDictionary(x => x.v.VersionId, x => x);
        var depById = depVersions.ToDictionary(d => d.VersionId, d => d);

        var finalDerivedIds = new HashSet<string>();

        foreach (var gen in generations.Distinct())
        {
            var isFinal = ReferenceEquals(gen, generations[^1]);
            if (clock is null || gen.ThresholdVersionId is null ||
                !thresholdVersionsById.TryGetValue(gen.ThresholdVersionId, out var tv))
                continue;

            var timelineIds = new Dictionary<string, string>();
            foreach (var (rail, svId) in gen.RailSourceVersions.OrderBy(x => x.Key))
            {
                if (!sourceVersionsById.TryGetValue(svId, out var sv)) continue;
                var railThreshold = tv.Payload.Rails.FirstOrDefault(r => r.Rail == rail);
                if (railThreshold is null) continue;

                var fingerprint = Ids.Fingerprint(new { algo = Algo.Version, railThreshold, clock });
                var inputs = new Dictionary<string, string>
                {
                    ["source"] = svId, ["thresholds"] = gen.ThresholdVersionId, ["rail"] = rail
                };
                var id = Ids.ContentId("der", $"rail-timeline|{Algo.Version}|{svId}|{fingerprint}");
                timelineIds[rail] = id;
                if (isFinal) finalDerivedIds.Add(id);
                if (!derived.ContainsKey(id))
                {
                    var norm = Waveform.Normalize(svId, sv.v.Payload.Samples);
                    var usable = Waveform.UsableSamples(norm);
                    var timeline = StateMachine.Evaluate(rail, usable, railThreshold, clock);
                    derived[id] = new DerivedResult(id, "rail-timeline", rail, Algo.Version,
                        fingerprint, inputs, JsonUtil.Canon(timeline), false, null);
                }
            }

            if (gen.DependencyVersionId is not null && depById.TryGetValue(gen.DependencyVersionId, out var dv))
            {
                var cycleInputs = new Dictionary<string, string> { ["dependencies"] = gen.DependencyVersionId };
                var cycleId = Ids.ContentId("der", $"cycle-analysis|{Algo.Version}|{gen.DependencyVersionId}");
                if (isFinal) finalDerivedIds.Add(cycleId);
                if (!derived.ContainsKey(cycleId))
                {
                    var analysis = DependencyGraph.Analyze(dv.Payload.Edges);
                    derived[cycleId] = new DerivedResult(cycleId, "cycle-analysis", "graph", Algo.Version,
                        Ids.Fingerprint(dv.Payload), cycleInputs, JsonUtil.Canon(analysis), false, null);
                }

                var seqInputs = new Dictionary<string, string>(timelineIds.Select(kv =>
                    new KeyValuePair<string, string>($"timeline:{kv.Key}", kv.Value)))
                { ["dependencies"] = gen.DependencyVersionId };
                var seqFingerprint = Ids.Fingerprint(new { algo = Algo.Version, timelineIds, deps = dv.Payload, clock });
                var seqId = Ids.ContentId("der", $"sequence-evaluation|{Algo.Version}|{seqFingerprint}");
                if (isFinal) finalDerivedIds.Add(seqId);
                if (!derived.ContainsKey(seqId))
                {
                    var timelines = timelineIds
                        .Where(kv => derived.ContainsKey(kv.Value))
                        .ToDictionary(kv => kv.Key, kv => JsonUtil.Parse<RailTimeline>(derived[kv.Value].PayloadJson));
                    var evaluation = SequenceEvaluator.Evaluate(timelines, dv.Payload.Edges, clock);
                    derived[seqId] = new DerivedResult(seqId, "sequence-evaluation", "sequence", Algo.Version,
                        seqFingerprint, seqInputs, JsonUtil.Canon(evaluation), false, null);
                }
            }

        }

        // 标记当前/过期：过期结果仍可查看，但不能冒充当前结果。
        var derivedList = derived.Values.Select(d =>
        {
            var current = finalDerivedIds.Contains(d.Id);
            var supersededBy = current ? null : derived.Values
                .FirstOrDefault(o => o.Kind == d.Kind && o.Subject == d.Subject && finalDerivedIds.Contains(o.Id))?.Id;
            return d with { Current = current, StaleReason = current ? null : $"superseded by {supersededBy ?? "newer input versions"}" };
        }).OrderBy(d => d.Kind).ThenBy(d => d.Subject).ToList();

        // ---- 决定 / 冲突 / 候选状态 ----
        var decisionViews = decisions.Select(d => d with { Reverted = reverted.Contains(d.DecisionId) }).ToList();
        var activeDecisions = decisionViews.Where(d => !d.Reverted).ToList();

        var conflicts = activeDecisions.GroupBy(d => d.CandidateId)
            .Where(g => g.Select(d => d.Verdict).Distinct().Count() > 1)
            .Select(g =>
            {
                var res = resolutions.LastOrDefault(r => r.CandidateId == g.Key);
                return new ConflictView(g.Key, g.Select(d => d.DecisionId).ToList(), res is not null, res?.ChosenDecisionId);
            }).ToList();

        var allCandidates = new Dictionary<string, Candidate>();
        string? currentSeqEvalId = finalDerivedIds.FirstOrDefault(id =>
            derived.TryGetValue(id, out var d) && d.Kind == "sequence-evaluation");
        foreach (var d in derivedList.Where(d => d.Kind == "sequence-evaluation"))
            foreach (var c in JsonUtil.Parse<SequenceEvaluation>(d.PayloadJson).Candidates)
                allCandidates.TryAdd(c.Id, c);

        var candidateViews = allCandidates.Values.Select(c =>
        {
            var cds = activeDecisions.Where(d => d.CandidateId == c.Id).ToList();
            var res = resolutions.LastOrDefault(r => r.CandidateId == c.Id);
            CandidateStatus status;
            if (res is not null)
            {
                var chosen = cds.FirstOrDefault(d => d.DecisionId == res.ChosenDecisionId);
                status = chosen?.Verdict switch
                {
                    Verdict.Accept => CandidateStatus.Accepted,
                    Verdict.Reject => CandidateStatus.Rejected,
                    Verdict.Adjust => CandidateStatus.Adjusted,
                    _ => CandidateStatus.Open
                };
            }
            else if (cds.Count > 0 && cds.Select(d => d.Verdict).Distinct().Count() == 1)
            {
                status = cds[0].Verdict switch
                {
                    Verdict.Accept => CandidateStatus.Accepted,
                    Verdict.Reject => CandidateStatus.Rejected,
                    _ => CandidateStatus.Adjusted
                };
            }
            else if (cds.Count > 0) status = CandidateStatus.Open; // 冲突未决
            else status = CandidateStatus.Open;

            var inCurrent = currentSeqEvalId is not null &&
                JsonUtil.Parse<SequenceEvaluation>(derived[currentSeqEvalId].PayloadJson)
                    .Candidates.Any(x => x.Id == c.Id);
            if (!inCurrent && status == CandidateStatus.Open) status = CandidateStatus.Superseded;

            return new CandidateView(c with { Status = status }, status, cds.Select(d => d.DecisionId).ToList());
        }).OrderBy(c => c.Candidate.AtNs).ToList();

        var sourceViews = sources.Select(kv =>
        {
            var b = kv.Value;
            var current = b.Versions.First(v => v.VersionId == b.CurrentVersionId);
            var norm = Waveform.Normalize(b.CurrentVersionId, current.Payload.Samples);
            return new SourceView(kv.Key, b.Name, b.Kind, b.Rail, b.Versions, b.CurrentVersionId, norm.Diagnostics);
        }).OrderBy(s => s.Rail).ToList();

        return new CaseView(
            caseId, title, clock,
            events.LastOrDefault()?.Seq ?? 0, latestSeq,
            sourceViews,
            thresholdSets.Select(kv => new ThresholdSetView(kv.Key, kv.Value.Versions, kv.Value.CurrentVersionId)).ToList(),
            depVersions, currentDepVersionId,
            derivedList, decisionViews, candidateViews, conflicts, events);
    }
}
