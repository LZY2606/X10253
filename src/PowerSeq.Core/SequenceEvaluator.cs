namespace PowerSeq.Core;

public enum CandidateStatus { Open, Accepted, Rejected, Adjusted, Superseded }

/// <summary>候选：无法唯一决定时全部保留，等待人工裁定；每个候选带理由与涉及对象。</summary>
public sealed record Candidate(
    string Id,
    string Kind,
    string Reason,
    IReadOnlyList<string> Involved,
    double AtNs,
    CandidateStatus Status = CandidateStatus.Open);

public sealed record Violation(
    string Kind,
    string Subject,
    double AtNs,
    string Evidence,
    IReadOnlyDictionary<string, string> Data);

public sealed record SequenceEvaluation(
    IReadOnlyList<Violation> Violations,
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<Judgment> Judgments)
{
    public Violation? FirstViolation => Violations.OrderBy(v => v.AtNs).FirstOrDefault();
}

/// <summary>
/// 启动/关机时序评估：依赖边 From->To 表示 From 必须先于 To 上电（Good），
/// 关机时逆序（To 先跌出 Good，From 后跌出）。
/// 到达时间差落在 SimultaneityWindowNs 内记为“同时到达”候选而非违约。
/// </summary>
public static class SequenceEvaluator
{
    public const string RuleStartupOrder = "sequence.startup-order";
    public const string RuleShutdownReverse = "sequence.shutdown-reverse-order";
    public const string RuleSimultaneous = "sequence.simultaneous-arrival";

    public static SequenceEvaluation Evaluate(
        IReadOnlyDictionary<string, RailTimeline> timelines,
        IReadOnlyList<DependencyEdge> edges,
        ClockSpec clock)
    {
        var violations = new List<Violation>();
        var candidates = new List<Candidate>();
        var judgments = new List<Judgment>();

        foreach (var edge in edges)
        {
            if (!timelines.TryGetValue(edge.FromRail, out var from) ||
                !timelines.TryGetValue(edge.ToRail, out var to))
                continue;

            // 启动：from 必须先于 to 进入 Good。
            var fromGood = from.FirstGoodNs;
            var toGood = to.FirstGoodNs;
            if (fromGood is null || toGood is null)
            {
                var missing = fromGood is null ? edge.FromRail : edge.ToRail;
                candidates.Add(new Candidate(
                    Ids.ContentId("cand", $"startup-unknown|{edge}|{missing}"),
                    "startup-unknown",
                    $"rail {missing} never reaches Good; ordering for {edge} cannot be decided",
                    new[] { edge.FromRail, edge.ToRail }, 0));
                judgments.Add(new Judgment(RuleStartupOrder, edge.ToString(), "unknown",
                    new Dictionary<string, string> { ["missing"] = missing }));
            }
            else
            {
                var delta = toGood.Value - fromGood.Value;
                if (delta < -clock.SimultaneityWindowNs)
                {
                    violations.Add(new Violation("startup-order", edge.ToString(), toGood.Value,
                        $"{edge.ToRail} Good at {toGood:0.##}ns but {edge.FromRail} Good at {fromGood:0.##}ns (delta {delta:0.##}ns)",
                        new Dictionary<string, string>
                        {
                            ["edge"] = edge.ToString(),
                            ["fromGoodNs"] = fromGood.Value.ToString("R"),
                            ["toGoodNs"] = toGood.Value.ToString("R"),
                            ["deltaNs"] = delta.ToString("R")
                        }));
                    judgments.Add(new Judgment(RuleStartupOrder, edge.ToString(), "violation",
                        new Dictionary<string, string> { ["deltaNs"] = delta.ToString("R") }));
                }
                else if (Math.Abs(delta) <= clock.SimultaneityWindowNs)
                {
                    candidates.Add(new Candidate(
                        Ids.ContentId("cand", $"simultaneous|{edge}|{fromGood:0.##}|{toGood:0.##}"),
                        "simultaneous-arrival",
                        $"{edge.FromRail} and {edge.ToRail} reach Good within simultaneity window {clock.SimultaneityWindowNs:0.##}ns (delta {delta:0.##}ns); both orderings are legal candidates",
                        new[] { edge.FromRail, edge.ToRail }, Math.Min(fromGood.Value, toGood.Value)));
                    judgments.Add(new Judgment(RuleSimultaneous, edge.ToString(), "ambiguous",
                        new Dictionary<string, string>
                        {
                            ["deltaNs"] = delta.ToString("R"),
                            ["windowNs"] = clock.SimultaneityWindowNs.ToString("R")
                        }));
                }
                else
                {
                    judgments.Add(new Judgment(RuleStartupOrder, edge.ToString(), "pass",
                        new Dictionary<string, string> { ["deltaNs"] = delta.ToString("R") }));
                }
            }

            // 关机：逆序，to 必须先于 from 跌出 Good。
            var fromFall = from.FirstFallNs;
            var toFall = to.FirstFallNs;
            if (fromFall is not null && toFall is not null)
            {
                var delta = fromFall.Value - toFall.Value; // to 先跌 => delta > 0
                if (delta < -clock.SimultaneityWindowNs)
                {
                    violations.Add(new Violation("shutdown-reverse-order", edge.ToString(), fromFall.Value,
                        $"{edge.FromRail} falls at {fromFall:0.##}ns before {edge.ToRail} falls at {toFall:0.##}ns (shutdown must be reverse of startup)",
                        new Dictionary<string, string>
                        {
                            ["edge"] = edge.ToString(),
                            ["fromFallNs"] = fromFall.Value.ToString("R"),
                            ["toFallNs"] = toFall.Value.ToString("R"),
                            ["deltaNs"] = delta.ToString("R")
                        }));
                    judgments.Add(new Judgment(RuleShutdownReverse, edge.ToString(), "violation",
                        new Dictionary<string, string> { ["deltaNs"] = delta.ToString("R") }));
                }
                else if (Math.Abs(delta) <= clock.SimultaneityWindowNs)
                {
                    candidates.Add(new Candidate(
                        Ids.ContentId("cand", $"simultaneous-fall|{edge}|{fromFall:0.##}|{toFall:0.##}"),
                        "simultaneous-arrival",
                        $"{edge.FromRail} and {edge.ToRail} fall within simultaneity window during shutdown (delta {delta:0.##}ns)",
                        new[] { edge.FromRail, edge.ToRail }, Math.Min(fromFall.Value, toFall.Value)));
                }
                else
                {
                    judgments.Add(new Judgment(RuleShutdownReverse, edge.ToString(), "pass",
                        new Dictionary<string, string> { ["deltaNs"] = delta.ToString("R") }));
                }
            }
        }

        return new SequenceEvaluation(
            violations.OrderBy(v => v.AtNs).ToList(),
            candidates.OrderBy(c => c.AtNs).ToList(),
            judgments);
    }
}
