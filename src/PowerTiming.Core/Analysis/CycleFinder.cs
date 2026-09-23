using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Analysis;

/// <summary>
/// Dependency-graph cycle analysis. It does not merely report "cyclic":
/// it returns every minimum contradictory edge set (minimum feedback arc set),
/// because there may be multiple equally legal ways to break the contradiction.
/// Graphs in this tool are deliberately small, so exhaustive bounded enumeration
/// is used instead of an unexplained heuristic.
/// </summary>
public sealed class CycleFinder
{
    public CycleAnalysisResult Analyze(IReadOnlyCollection<DependencyEdge> edges, IReadOnlySet<string> waivedEdgeIds)
    {
        var active = edges.Where(edge => !waivedEdgeIds.Contains(edge.EdgeId)).ToList();
        var cycles = FindSimpleCycles(active);
        if (cycles.Count == 0)
        {
            return new CycleAnalysisResult(false, [], [], active.Select(e => e.EdgeId).ToList(), waivedEdgeIds.ToList(),
                "active dependency graph is acyclic after applying accepted waiver decisions");
        }

        var minimumSets = EnumerateMinimumFeedbackSets(active);
        return new CycleAnalysisResult(
            true,
            cycles,
            minimumSets,
            active.Select(edge => edge.EdgeId).ToList(),
            waivedEdgeIds.ToList(),
            "minimum contradictory edge set = smallest edge removal set that makes every cycle acyclic");
    }

    private static List<List<string>> FindSimpleCycles(IReadOnlyList<DependencyEdge> edges)
    {
        var adjacency = edges.GroupBy(edge => edge.FromRail).ToDictionary(group => group.Key, group => group.Select(edge => edge.ToRail).ToList());
        var cycles = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in edges.Select(edge => edge.FromRail).Distinct().OrderBy(id => id, StringComparer.Ordinal))
        {
            var stack = new Stack<string>();
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            Visit(start, start);

            void Visit(string node, string root)
            {
                stack.Push(node);
                onStack.Add(node);
                foreach (var next in adjacency.GetValueOrDefault(node, []).OrderBy(id => id, StringComparer.Ordinal))
                {
                    if (next == root && stack.Count > 0)
                    {
                        var cycle = stack.Reverse().Concat(new[] { root }).ToList();
                        var canonical = CanonicalCycle(cycle);
                        if (seen.Add(canonical))
                        {
                            cycles.Add(cycle);
                        }
                    }
                    else if (!onStack.Contains(next) && string.CompareOrdinal(next, root) >= 0 && !stack.Contains(next))
                    {
                        Visit(next, root);
                    }
                }

                onStack.Remove(node);
                stack.Pop();
            }
        }

        return cycles;
    }

    private static string CanonicalCycle(IReadOnlyList<string> cycle)
    {
        var vertices = cycle.Take(cycle.Count - 1).ToList();
        var rotations = Enumerable.Range(0, vertices.Count)
            .Select(offset => string.Join(">", vertices.Skip(offset).Concat(vertices.Take(offset))));
        return rotations.Min(StringComparer.Ordinal)!;
    }

    private static List<FeedbackSetCandidate> EnumerateMinimumFeedbackSets(IReadOnlyList<DependencyEdge> edges)
    {
        var edgeIds = edges.Select(edge => edge.EdgeId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        for (var size = 1; size <= edgeIds.Count; size++)
        {
            var hits = new List<FeedbackSetCandidate>();
            foreach (var subset in Combinations(edgeIds, size))
            {
                var remaining = edges.Where(edge => !subset.Contains(edge.EdgeId)).ToList();
                if (!HasAnyCycle(remaining))
                {
                    var ordered = subset.OrderBy(id => id, StringComparer.Ordinal).ToList();
                    var setId = Hashing.StableId("fbs", string.Join("|", ordered), 12);
                    hits.Add(new FeedbackSetCandidate(
                        setId,
                        ordered,
                        size,
                        $"Removing {size} edge(s) makes the active dependency graph acyclic; exhaustive subset enumeration proved no smaller set works.",
                        false));
                }
            }

            if (hits.Count > 0)
            {
                var unique = hits.Count == 1;
                return hits.Select(hit => hit with { UniquelyMinimum = unique }).ToList();
            }
        }

        return [];
    }

    private static bool HasAnyCycle(IReadOnlyList<DependencyEdge> edges)
    {
        var adjacency = edges.GroupBy(edge => edge.FromRail).ToDictionary(group => group.Key, group => group.Select(edge => edge.ToRail).ToList());
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in edges.SelectMany(edge => new[] { edge.FromRail, edge.ToRail }).Distinct())
        {
            state[node] = 0;
        }

        foreach (var node in state.Keys)
        {
            if (Dfs(node))
            {
                return true;
            }
        }

        return false;

        bool Dfs(string current)
        {
            state[current] = 1;
            foreach (var next in adjacency.GetValueOrDefault(current, []))
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    return true;
                }

                if (state.GetValueOrDefault(next) == 0 && Dfs(next))
                {
                    return true;
                }
            }

            state[current] = 2;
            return false;
        }
    }

    private static IEnumerable<List<string>> Combinations(IReadOnlyList<string> values, int size)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }

        if (values.Count < size)
        {
            yield break;
        }

        var indices = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return indices.Select(index => values[index]).ToList();
            var cursor = size - 1;
            while (cursor >= 0 && indices[cursor] == values.Count - size + cursor)
            {
                cursor--;
            }

            if (cursor < 0)
            {
                yield break;
            }

            indices[cursor]++;
            for (var next = cursor + 1; next < size; next++)
            {
                indices[next] = indices[next - 1] + 1;
            }
        }
    }
}
