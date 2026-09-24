namespace PowerSequencer.Domain;

/// <summary>
/// Dependency graph analysis: per-edge timing verdicts and, for cyclic
/// dependencies, the minimal conflicting edge sets (minimum feedback edge
/// sets). When several minimum sets exist, all are kept as candidates.
/// </summary>
public static class Graph
{
    public const string KindRequiresGoodBeforeEnable = "requires-good-before-enable";
    public const string KindMaxDelay = "max-delay";

    private static bool IsAcyclic(List<string> nodes, List<DependencyEdge> edges)
    {
        var indeg = nodes.ToDictionary(n => n, _ => 0);
        var adj = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (var e in edges)
        {
            if (!indeg.ContainsKey(e.To) || !indeg.ContainsKey(e.From)) continue;
            indeg[e.To]++;
            adj[e.From].Add(e.To);
        }
        var queue = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var seen = 0;
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            seen++;
            foreach (var m in adj[n])
                if (--indeg[m] == 0) queue.Enqueue(m);
        }
        return seen == nodes.Count;
    }

    /// <summary>
    /// Minimum feedback edge sets: smallest sets of edges whose removal makes
    /// the graph acyclic. All minimum-size solutions are returned (brute force,
    /// capped for small hardware dependency graphs).
    /// </summary>
    public static CycleConflict? MinimalConflictEdgeSets(List<DependencyEdge> edges, int maxEdges = 20)
    {
        var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.Ordinal).ToList();
        if (IsAcyclic(nodes, edges)) return null;
        if (edges.Count > maxEdges)
            return new CycleConflict(new List<List<DependencyEdge>>(),
                $"graph has {edges.Count} edges > cap {maxEdges}; minimal set not computed");

        for (var size = 1; size <= edges.Count; size++)
        {
            var solutions = new List<List<DependencyEdge>>();
            foreach (var combo in Combinations(edges.Count, size))
            {
                var removed = combo.Select(i => edges[i]).ToList();
                var remaining = edges.Where((_, i) => !combo.Contains(i)).ToList();
                if (IsAcyclic(nodes, remaining))
                    solutions.Add(removed);
            }
            if (solutions.Count > 0)
            {
                var reason = solutions.Count == 1
                    ? $"unique minimal conflicting edge set of size {size}"
                    : $"{solutions.Count} minimal conflicting edge sets of size {size}; all kept as candidates";
                return new CycleConflict(solutions, reason);
            }
        }
        return null; // unreachable for finite graphs
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.ToArray();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    /// <summary>
    /// Verdict per dependency edge. goodTime/enableTime maps may lack entries
    /// (unknown); simultaneous arrival within tolerance yields "ambiguous".
    /// </summary>
    public static List<EdgeVerdict> Verdicts(
        List<DependencyEdge> edges,
        IReadOnlyDictionary<string, Engine.EdgeTime> riseTimes,
        List<Diagnostic> diags)
    {
        var verdicts = new List<EdgeVerdict>();
        foreach (var e in edges)
        {
            var hasFrom = riseTimes.TryGetValue(e.From, out var from);
            var hasTo = riseTimes.TryGetValue(e.To, out var to);
            if (!hasFrom || !hasTo)
            {
                verdicts.Add(new EdgeVerdict(e, "unknown", null,
                    $"missing rise time for {(hasFrom ? e.To : e.From)}; cannot decide"));
                diags.Add(new("edge-unknown", $"edge {e.From}->{e.To}: rise time unknown", null, null));
                continue;
            }
            var tol = from!.Tolerance + to!.Tolerance;
            var diff = to.Time - from.Time; // positive: 'from' rose first
            switch (e.Kind)
            {
                case KindRequiresGoodBeforeEnable:
                    if (diff > tol)
                        verdicts.Add(new EdgeVerdict(e, "satisfied", to.Time,
                            $"{e.From} good {Engine.F(diff)}s before {e.To} enable"));
                    else if (diff < -tol)
                        verdicts.Add(new EdgeVerdict(e, "violated", to.Time,
                            $"{e.To} enabled {Engine.F(-diff)}s before {e.From} was good"));
                    else
                        verdicts.Add(new EdgeVerdict(e, "ambiguous", to.Time,
                            $"simultaneous arrival within tolerance {Engine.F(tol)}s; order undecidable"));
                    break;
                case KindMaxDelay:
                    if (!Units.TryNormalize(e.MaxDelay!, UnitKind.Time, out var maxDelay, out var err))
                    {
                        verdicts.Add(new EdgeVerdict(e, "unknown", null, $"bad maxDelay unit: {err}"));
                        break;
                    }
                    if (diff >= -tol && diff <= maxDelay - tol)
                        verdicts.Add(new EdgeVerdict(e, "satisfied", to.Time,
                            $"delay {Engine.F(diff)}s within max {Engine.F(maxDelay)}s"));
                    else if (diff > maxDelay + tol || diff < -tol)
                        verdicts.Add(new EdgeVerdict(e, "violated", to.Time,
                            $"delay {Engine.F(diff)}s outside [0, {Engine.F(maxDelay)}s]"));
                    else
                        verdicts.Add(new EdgeVerdict(e, "ambiguous", to.Time,
                            $"delay {Engine.F(diff)}s within tolerance of limit {Engine.F(maxDelay)}s"));
                    break;
                default:
                    verdicts.Add(new EdgeVerdict(e, "unknown", null, $"unknown edge kind '{e.Kind}'"));
                    diags.Add(new("edge-kind-unknown", $"edge {e.From}->{e.To} kind '{e.Kind}'", null, null));
                    break;
            }
        }
        return verdicts;
    }
}
