namespace PowerSeq.Core;

/// <summary>循环依赖分析结果：SCC、以及全部最小矛盾边集（无法唯一决定时保留所有合法候选）。</summary>
public sealed record CycleAnalysis(
    bool Acyclic,
    IReadOnlyList<IReadOnlyList<string>> StrongComponents,
    IReadOnlyList<IReadOnlyList<DependencyEdge>> MinimumConflictEdgeSets,
    IReadOnlyList<Judgment> Judgments);

public static class DependencyGraph
{
    public const string RuleMinConflict = "cycle.min-conflict-edge-set";

    public static CycleAnalysis Analyze(IReadOnlyList<DependencyEdge> edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.FromRail, e.ToRail }).Distinct().OrderBy(x => x).ToList();
        var sccs = StronglyConnected(nodes, edges)
            .Where(c => c.Count > 1 || edges.Any(e => e.FromRail == c[0] && e.ToRail == c[0]))
            .Select(c => (IReadOnlyList<string>)c.OrderBy(x => x).ToList())
            .ToList();

        if (sccs.Count == 0)
            return new CycleAnalysis(true, sccs, Array.Empty<IReadOnlyList<DependencyEdge>>(),
                new[] { new Judgment(RuleMinConflict, "graph", "acyclic", new Dictionary<string, string>()) });

        // 最小反馈边集（精确枚举，图小故可行）：从小到大枚举边子集，移除后无环即为一组解。
        var solutions = new List<IReadOnlyList<DependencyEdge>>();
        for (var k = 1; k <= edges.Count; k++)
        {
            foreach (var combo in Combinations(edges, k))
            {
                var remaining = edges.Except(combo).ToList();
                if (IsAcyclic(remaining))
                    solutions.Add(combo.OrderBy(e => e.ToString()).ToList());
            }
            if (solutions.Count > 0) break; // 只保留最小规模
        }

        var judgments = new List<Judgment>
        {
            new(RuleMinConflict, "graph", solutions.Count == 1 ? "unique" : "ambiguous",
                new Dictionary<string, string>
                {
                    ["strongComponents"] = string.Join(";", sccs.Select(c => string.Join(",", c))),
                    ["minimumSetSize"] = solutions.Count > 0 ? solutions[0].Count.ToString() : "0",
                    ["candidateCount"] = solutions.Count.ToString(),
                    ["candidates"] = string.Join(";", solutions.Select(s => string.Join(",", s.Select(e => e.ToString()))))
                })
        };
        return new CycleAnalysis(false, sccs, solutions, judgments);
    }

    private static bool IsAcyclic(IReadOnlyList<DependencyEdge> edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.FromRail, e.ToRail }).Distinct().ToList();
        return StronglyConnected(nodes, edges).All(c => c.Count == 1 &&
            !edges.Any(e => e.FromRail == c[0] && e.ToRail == c[0]));
    }

    private static List<List<string>> StronglyConnected(List<string> nodes, IReadOnlyList<DependencyEdge> edges)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        void Visit(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v); onStack.Add(v);
            foreach (var e in edges.Where(e => e.FromRail == v))
            {
                if (!index.ContainsKey(e.ToRail))
                {
                    Visit(e.ToRail);
                    low[v] = Math.Min(low[v], low[e.ToRail]);
                }
                else if (onStack.Contains(e.ToRail))
                    low[v] = Math.Min(low[v], index[e.ToRail]);
            }
            if (low[v] == index[v])
            {
                var comp = new List<string>();
                string w;
                do { w = stack.Pop(); onStack.Remove(w); comp.Add(w); } while (w != v);
                result.Add(comp);
            }
        }

        foreach (var n in nodes)
            if (!index.ContainsKey(n)) Visit(n);
        return result;
    }

    private static IEnumerable<List<DependencyEdge>> Combinations(IReadOnlyList<DependencyEdge> edges, int k)
    {
        var idx = new int[k];
        for (var i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => edges[i]).ToList();
            var pos = k - 1;
            while (pos >= 0 && idx[pos] == edges.Count - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (var i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
