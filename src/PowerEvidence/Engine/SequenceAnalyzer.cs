using PowerEvidence.Domain;

namespace PowerEvidence.Engine;

public sealed record SequenceFinding(
    string Kind, string Rail, double T, string Detail,
    string? EdgeBefore = null, string? EdgeAfter = null,
    IReadOnlyList<Candidate>? Candidates = null, string? AmbiguityId = null);

public sealed record SequenceReport(
    IReadOnlyList<SequenceFinding> Findings,
    IReadOnlyList<Ambiguity> Ambiguities,
    IReadOnlyList<string> Diagnostics)
{
    public SequenceFinding? FirstViolation =>
        Findings.Where(f => f.Kind is "order-violation" or "shutdown-violation")
            .OrderBy(f => f.T).FirstOrDefault();
}

/// <summary>
/// 序列分析（算法版本 sequence-check-v1）：启动顺序按依赖边检查，
/// 关机按逆序检查；同一采样节拍内到达判为"同时到达"并保留全部合法候选。
/// </summary>
public static class SequenceAnalyzer
{
    public const string AlgoVersion = "sequence-check-v1";

    public static SequenceReport Analyze(
        IReadOnlyList<RailAnalysis> rails, DependencyGraph graph, ClockSpec clock)
    {
        var findings = new List<SequenceFinding>();
        var ambiguities = new List<Ambiguity>();
        var diagnostics = new List<string>();
        var byRail = rails.ToDictionary(r => r.Rail);
        var simultWindow = double.IsNaN(clock.Period) ? 0 : clock.Period / 2 + clock.Error;

        foreach (var edge in graph.Edges)
        {
            if (!byRail.TryGetValue(edge.Before, out var a) || !byRail.TryGetValue(edge.After, out var b))
            {
                diagnostics.Add($"诊断: 依赖边 {edge.Before}→{edge.After} 引用了无波形的轨，跳过");
                continue;
            }

            // 启动顺序
            var riseA = a.FirstRise;
            var riseB = b.FirstRise;
            if (riseA is null || riseB is null)
            {
                diagnostics.Add($"诊断: 依赖边 {edge.Before}→{edge.After} 缺少上升事件（{(riseA is null ? edge.Before : edge.After)}），启动顺序未知");
            }
            else
            {
                var delta = riseB.Value - riseA.Value;
                if (delta < -simultWindow)
                {
                    findings.Add(new SequenceFinding("order-violation", edge.After, riseB.Value,
                        $"启动违约: {edge.After} 在 t={riseB} 上升，先于依赖 {edge.Before} 的 t={riseA}（超出时钟分辨率 {simultWindow:0.###}ms）",
                        edge.Before, edge.After));
                }
                else if (Math.Abs(delta) <= simultWindow && delta <= 0)
                {
                    var ambId = IdGen.ContentId("amb", $"simult|{edge.Before}|{edge.After}|{riseA}|{riseB}");
                    var amb = new Ambiguity(ambId, "simultaneous-arrival", edge.After,
                        $"同时到达: {edge.Before} 与 {edge.After} 在 t={riseA} 同一采样节拍内上升（时钟分辨率 {simultWindow:0.###}ms）",
                        new Candidate[]
                        {
                            new(0, "violation", $"按严格先后判定 {edge.Before}→{edge.After} 被违反"),
                            new(1, "tolerated", "同一节拍内到达，在采样时钟分辨率内无法区分，判为可接受")
                        });
                    ambiguities.Add(amb);
                    findings.Add(new SequenceFinding("simultaneous-arrival", edge.After, riseB.Value,
                        amb.Detail, edge.Before, edge.After, amb.Candidates, ambId));
                }
            }

            // 关机逆序
            var fallA = a.FirstFall;
            var fallB = b.FirstFall;
            if (fallA is null || fallB is null)
            {
                diagnostics.Add($"诊断: 依赖边 {edge.Before}→{edge.After} 缺少下降事件，关机顺序未知");
            }
            else
            {
                var delta = fallB.Value - fallA.Value;
                if (delta > simultWindow)
                {
                    findings.Add(new SequenceFinding("shutdown-violation", edge.Before, fallA.Value,
                        $"关机逆序违约: {edge.Before} 在 t={fallA} 先下降，而其依赖的 {edge.After} 直到 t={fallB} 才下降（应后于 {edge.After} 断电）",
                        edge.Before, edge.After));
                }
            }
        }

        return new SequenceReport(findings, ambiguities, diagnostics);
    }
}
