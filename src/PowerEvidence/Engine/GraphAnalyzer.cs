using PowerEvidence.Domain;

namespace PowerEvidence.Engine;

/// <summary>
/// 循环依赖分析：给出最小矛盾边集（最小反馈边集）。
/// 无法唯一决定时保留全部合法候选。小规模图精确枚举。
/// </summary>
public static class GraphAnalyzer
{
    public const string AlgoVersion = "dep-cycle-v1";

    public static IReadOnlyList<IReadOnlyList<DependencyEdge>> MinimalFeedbackEdgeSets(DependencyGraph graph)
    {
        var edges = graph.Edges;
        if (edges.Count == 0 || !HasCycle(edges)) return Array.Empty<IReadOnlyList<DependencyEdge>>();

        for (var size = 1; size <= edges.Count; size++)
        {
            var solutions = new List<IReadOnlyList<DependencyEdge>>();
            foreach (var combo in Combinations(edges, size))
            {
                var remaining = edges.Except(combo).ToList();
                if (!HasCycle(remaining)) solutions.Add(combo);
            }
            if (solutions.Count > 0) return solutions;
        }
        return Array.Empty<IReadOnlyList<DependencyEdge>>();
    }

    public static bool HasCycle(IReadOnlyList<DependencyEdge> edges)
    {
        var adj = edges.GroupBy(e => e.Before).ToDictionary(g => g.Key, g => g.Select(e => e.After).ToList());
        var state = new Dictionary<string, int>();
        var nodes = edges.SelectMany(e => new[] { e.Before, e.After }).Distinct();

        bool Dfs(string n)
        {
            state[n] = 1;
            if (adj.TryGetValue(n, out var nexts))
                foreach (var m in nexts)
                {
                    if (state.GetValueOrDefault(m) == 1) return true;
                    if (state.GetValueOrDefault(m) == 0 && Dfs(m)) return true;
                }
            state[n] = 2;
            return false;
        }

        foreach (var n in nodes)
            if (state.GetValueOrDefault(n) == 0 && Dfs(n)) return true;
        return false;
    }

    private static IEnumerable<List<DependencyEdge>> Combinations(IReadOnlyList<DependencyEdge> edges, int size)
    {
        var idx = new int[size];
        for (var i = 0; i < size; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => edges[i]).ToList();
            var pos = size - 1;
            while (pos >= 0 && idx[pos] == edges.Count - size + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (var i = pos + 1; i < size; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
