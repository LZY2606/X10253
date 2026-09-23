namespace PowerSeq.Domain;

public record CycleReport(
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<IReadOnlyList<DependencyEdge>> MinimalConflictSets);

public static class DependencyGraph
{
    /// <summary>All elementary cycles (small graphs; DFS enumeration).</summary>
    public static List<List<string>> FindCycles(IReadOnlyList<DependencyEdge> edges)
    {
        var adj = edges.GroupBy(e => e.Before, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.After).ToList(), StringComparer.OrdinalIgnoreCase);
        var cycles = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in adj.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var stack = new List<string> { start };
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            Dfs(start);
            void Dfs(string node)
            {
                if (!adj.TryGetValue(node, out var nexts)) return;
                foreach (var next in nexts)
                {
                    if (next.Equals(start, StringComparison.OrdinalIgnoreCase) && stack.Count > 1)
                    {
                        var cyc = string.Join(">", stack.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                        if (seen.Add(cyc)) cycles.Add(new List<string>(stack));
                    }
                    else if (!onStack.Contains(next) && string.CompareOrdinal(next, start) >= 0)
                    {
                        stack.Add(next); onStack.Add(next);
                        Dfs(next);
                        stack.RemoveAt(stack.Count - 1); onStack.Remove(next);
                    }
                }
            }
        }
        return cycles;
    }

    /// <summary>
    /// Minimal conflicting edge sets: all minimum-cardinality sets of edges whose
    /// removal breaks every cycle (minimum feedback edge sets, kept as candidates
    /// when the choice is not unique).
    /// </summary>
    public static CycleReport AnalyzeCycles(IReadOnlyList<DependencyEdge> edges)
    {
        var cycles = FindCycles(edges);
        if (cycles.Count == 0) return new CycleReport(cycles, new List<IReadOnlyList<DependencyEdge>>());

        var cycleEdgeSets = cycles.Select(cyc =>
        {
            var set = new List<DependencyEdge>();
            for (var i = 0; i < cyc.Count; i++)
            {
                var a = cyc[i];
                var b = cyc[(i + 1) % cyc.Count];
                var edge = edges.First(e => e.Before.Equals(a, StringComparison.OrdinalIgnoreCase)
                                         && e.After.Equals(b, StringComparison.OrdinalIgnoreCase));
                set.Add(edge);
            }
            return set;
        }).ToList();

        var candidateEdges = cycleEdgeSets.SelectMany(s => s).Distinct().ToList();
        var best = new List<IReadOnlyList<DependencyEdge>>();
        for (var size = 1; size <= candidateEdges.Count; size++)
        {
            foreach (var combo in Combinations(candidateEdges, size))
            {
                if (cycleEdgeSets.All(cs => cs.Any(combo.Contains)))
                    best.Add(combo);
            }
            if (best.Count > 0) break; // minimum cardinality reached
        }
        return new CycleReport(cycles, best);
    }

    private static IEnumerable<List<DependencyEdge>> Combinations(List<DependencyEdge> items, int k)
    {
        var idx = new int[k];
        for (var i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => items[i]).ToList();
            var p = k - 1;
            while (p >= 0 && idx[p] == items.Count - k + p) p--;
            if (p < 0) yield break;
            idx[p]++;
            for (var i = p + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
