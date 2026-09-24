namespace PowerSeq.Domain;

/// <summary>
/// 循环依赖检测：枚举全部简单环，并计算最小矛盾边集
/// （删除后可使图无环的最小边集合；存在多个时全部保留为候选）。
/// </summary>
public static class CycleFinder
{
    public static Analyzer.CycleConflict Analyze(IReadOnlyList<DepEdge> edges)
    {
        var adj = edges.GroupBy(e => e.Before)
            .ToDictionary(g => g.Key, g => g.Select(e => e.After).Distinct().OrderBy(x => x).ToList());
        var nodes = edges.Select(e => e.Before).Concat(edges.Select(e => e.After)).Distinct().OrderBy(x => x).ToList();

        // 枚举简单环（以环中最小节点为起点，避免重复）
        var cycles = new List<List<string>>(); // 每个环 = 排序后的边 key 列表
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in nodes)
        {
            var stack = new List<string> { start };
            var onPath = new HashSet<string> { start };
            void Dfs(string node)
            {
                if (!adj.TryGetValue(node, out var nexts)) return;
                foreach (var next in nexts)
                {
                    if (next == start && stack.Count >= 1)
                    {
                        var cycNodes = stack.ToList();
                        var edgeKeys = new List<string>();
                        for (var i = 0; i < cycNodes.Count; i++)
                            edgeKeys.Add($"{cycNodes[i]}->{cycNodes[(i + 1) % cycNodes.Count]}");
                        edgeKeys.Sort(StringComparer.Ordinal);
                        var key = string.Join("|", edgeKeys);
                        if (seen.Add(key)) cycles.Add(edgeKeys);
                    }
                    else if (!onPath.Contains(next) && string.CompareOrdinal(next, start) >= 0)
                    {
                        stack.Add(next);
                        onPath.Add(next);
                        Dfs(next);
                        stack.RemoveAt(stack.Count - 1);
                        onPath.Remove(next);
                    }
                }
            }
            Dfs(start);
        }

        if (cycles.Count == 0)
            return new Analyzer.CycleConflict(Array.Empty<IReadOnlyList<string>>(), Array.Empty<IReadOnlyList<string>>());

        // 最小碰集（minimum hitting set）：从小到大枚举
        var universe = cycles.SelectMany(c => c).Distinct().OrderBy(x => x).ToList();
        var minSets = new List<IReadOnlyList<string>>();
        for (var size = 1; size <= universe.Count && minSets.Count == 0; size++)
        {
            foreach (var combo in Combinations(universe, size))
            {
                var set = combo.ToHashSet(StringComparer.Ordinal);
                if (cycles.All(c => c.Any(set.Contains)))
                    minSets.Add(combo);
            }
        }
        return new Analyzer.CycleConflict(cycles, minSets);
    }

    private static IEnumerable<List<string>> Combinations(List<string> items, int size)
    {
        var idx = new int[size];
        for (var i = 0; i < size; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => items[i]).ToList();
            var p = size - 1;
            while (p >= 0 && idx[p] == items.Count - size + p) p--;
            if (p < 0) yield break;
            idx[p]++;
            for (var i = p + 1; i < size; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
